using Hex.Core.Git;

namespace Hex.Dawn;

// Git in the editor. Which repositories the workspace and the open files are in, what git thinks of every file in them,
// kept up as files change, as git itself changes (a commit or a switch typed in the terminal moves HEAD and the index,
// which the workspace's watching leaves out), and as files are saved. Everything git is asked goes on in the
// background, one ask per repository at a time, and the answers come back on the editor's thread where the explorer,
// the tabs, the gutter and the status bar read them.
internal sealed partial class HexGit
{
    // How long after the last change before git is asked again, a build writing a hundred files is one look
    private const float SETTLE = 0.4f;

    // How often HEAD and the index are looked at for git having moved, a stat or two each
    private const float LOOK_EVERY = 1.0f;

    private readonly DawnContext context;

    // Every repository found, by its root, and the ones paths were found to be in, null for none
    private readonly Dictionary<string, Repository> repositories = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, Repository?> foundFor = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, Repository?> foundForPath = new(PlatformPaths.Comparer);

    private float lookIn;

    /// <summary>Raised on the editor's thread when what git thinks of anything changed, for whatever shows it to draw again.</summary>
    public event Action? Changed;

    /// <summary>Goes up every time git says something new about any repository, for whatever only wants to look again then.</summary>
    public int Version { get; private set; }

    public HexGit(DawnContext context)
    {
        this.context = context;
        FollowDocuments();
        FollowForBlame();
        BuildSourceControl();
        BuildSync();
        context.Workspace.FilesChanged += files =>
        {
            foreach (var (path, _) in files)
            {
                if (RepositoryOf(path) is { } repository)
                    repository.Dirty();
            }
        };
    }

    /// <summary>Whether git can be asked at all, there being a git on the PATH.</summary>
    public static bool Available => GitProcess.InstalledNow == true;

    /// <summary>The repository a file or a folder is in, null for none.</summary>
    public GitRepository? RepositoryFor(string path) => RepositoryOf(path)?.Git;

    /// <summary>What git last said about the repository a path is in, null for none or nothing said yet.</summary>
    public GitStatus? StatusFor(string path) => RepositoryOf(path)?.Status;

    /// <summary>What git thinks of a file, None for as committed or not in a repository.</summary>
    public GitFileState StateOf(string path) => StatusFor(path)?.StateOf(path) ?? default;

    /// <summary>The colour a folder takes from what's in it.</summary>
    public GitChange FolderChange(string folder) => StatusFor(folder)?.FolderChange(folder) ?? GitChange.None;

    /// <summary>Has git asked again about every repository, and every folder that had none looked at again for one.</summary>
    public void RefreshAll()
    {
        ForgetNone();
        LookDown(again: true);
        foreach (Repository repository in repositories.Values)
            repository.Dirty(now: true);
    }

    /// <summary>Has git asked again about the repository a path is in, after something Hex did to it (a save, a commit).</summary>
    public void Refresh(string path) => RepositoryOf(path)?.Dirty(now: true);

    /// <summary>Every tick, the repositories that changed asked about again once things settle, and git looked at for having moved.</summary>
    public void Update(float dt)
    {
        if (!Available)
            return;

        if ((lookIn -= dt) <= 0.0f)
        {
            lookIn = LOOK_EVERY;

            // A folder with no repository is looked at again, a git init after it was opened made one
            ForgetNone();
            foreach (string root in context.Workspace.Roots)
                RepositoryOf(root);
            LookDown();
            foreach (Repository repository in repositories.Values)
                repository.LookForMoves();
        }

        foreach (Repository repository in repositories.Values)
            repository.Update(dt);

        UpdateChanges(dt);
        UpdateBlame(dt);
        UpdateSync(dt);
    }

    // What's taken out of the caches of where a repository is, kept so a second with nothing to take makes no garbage
    private readonly List<string> forgetting = [];

    /// <summary>Helper method to forget every folder and path that had no repository, for it to be looked for again.</summary>
    private void ForgetNone()
    {
        foreach (var (key, value) in foundFor)
            if (value is null) forgetting.Add(key);
        foreach (string key in forgetting)
            foundFor.Remove(key);
        forgetting.Clear();

        foreach (var (key, value) in foundForPath)
            if (value is null) forgetting.Add(key);
        foreach (string key in forgetting)
            foundForPath.Remove(key);
        forgetting.Clear();
    }

    // The repository of a path, looked for once per folder
    private Repository? RepositoryOf(string path)
    {
        if (!Available || string.IsNullOrEmpty(path))
            return null;

        // Asked every tick for the file on screen, so a path asked before is answered without touching the disk
        if (foundForPath.TryGetValue(path, out Repository? asked))
            return asked;

        string folder = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        if (foundFor.TryGetValue(folder, out Repository? known))
        {
            foundForPath[path] = known;
            return known;
        }

        Repository? found = null;
        if (GitRepository.Find(folder) is { } git)
        {
            if (!repositories.TryGetValue(git.Root, out found))
            {
                found = new Repository(this, git);
                repositories[git.Root] = found;

                // Where git was as it's found, so a commit made before the first look still counts as a move
                found.LookForMoves();
            }
        }

        foundFor[folder] = found;
        foundForPath[path] = found;
        return found;
    }

    /// <summary>One repository, what git last said of it, and whether it needs asking again.</summary>
    private sealed class Repository(HexGit owner, GitRepository git)
    {
        public GitRepository Git { get; } = git;

        public GitStatus? Status { get; private set; }

        /// <summary>Whether it has a remote to fetch from and push to, as of the last look.</summary>
        public bool HasRemote { get; private set; }

        // Bumped every time git is asked about it, so what comes back from an older ask is let go of
        public int Generation { get; private set; }

        private float settleIn = 0.01f;
        private bool asking;
        private DateTime headTime, indexTime, refTime;

        /// <summary>Asked again soon, or straight away.</summary>
        public void Dirty(bool now = false) => settleIn = now ? 0.01f : Math.Max(settleIn, SETTLE);

        /// <summary>HEAD, the index and the branch's own ref, which a commit, a switch, a stage or a pull outside of Hex all touch.</summary>
        public void LookForMoves()
        {
            string dot = DotGit();
            DateTime head = Stamp(Path.Combine(dot, "HEAD"));
            DateTime index = Stamp(Path.Combine(dot, "index"));
            DateTime branchRef = Status?.Branch is { } branch ? Stamp(Path.Combine(dot, "refs", "heads", branch)) : default;
            if (head != headTime || index != indexTime || branchRef != refTime)
            {
                (headTime, indexTime, refTime) = (head, index, branchRef);
                Dirty(now: true);
            }
        }

        public void Update(float dt)
        {
            if (settleIn <= 0.0f || asking || (settleIn -= dt) > 0.0f)
                return;

            settleIn = 0.0f;
            asking = true;
            owner.context.Work.InBackground("git status", () => (Status: Git.Status(), Remote: Git.HasRemote()), found =>
            {
                asking = false;
                if (found.Status is not { } status)
                    return;

                Status = status;
                HasRemote = found.Remote;
                Generation++;
                owner.Version++;
                owner.Changed?.Invoke();
                owner.context.Redraw();
            }, quiet: true);
        }

        // Where git keeps its things, .git, or the folder a submodule's or a worktree's .git file points at
        private string DotGit()
        {
            string dot = Path.Combine(Git.Root, ".git");
            if (File.Exists(dot))
            {
                try
                {
                    string line = File.ReadAllText(dot).Trim();
                    if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                        return Path.GetFullPath(Path.Combine(Git.Root, line["gitdir:".Length..].Trim()));
                }
                catch (IOException) { }
            }

            return dot;
        }

        private static DateTime Stamp(string file)
        {
            try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default; }
            catch (IOException) { return default; }
        }
    }
}
