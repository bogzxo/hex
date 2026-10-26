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
        BuildStash();
        BuildMerge();
        BuildActive();
        context.Workspace.FilesChanged += files =>
        {
            foreach (var (path, change) in files)
            {
                if (RepositoryOf(path) is { } repository)
                    repository.Dirty();

                // Something made can be a clone or a folder git init is about to make a repository of, looked down for soon
                if (change == LanguageServerFileChange.Created)
                    LookDownSoon();
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

        // The file on screen in another repository makes that one the active one, a look only when the file changed
        FollowOpenFile();
        UpdateLookDown(dt);

        if ((lookIn -= dt) <= 0.0f)
        {
            lookIn = LOOK_EVERY;

            // A folder with no repository is looked at again, a git init after it was opened made one
            ForgetNone(all: false);
            IReadOnlyList<string> roots = context.Workspace.Roots;
            for (int i = 0; i < roots.Count; i++)
                RepositoryOf(roots[i]);
            LookDown();
            foreach (Repository repository in repositories.Values)
                repository.LookForMoves();

            // The file on screen can be in another repository now, whose merge or rebase the menu follows
            FollowOperation();
        }

        foreach (Repository repository in repositories.Values)
            repository.Update(dt);

        UpdateChanges(dt);
        UpdateBlame(dt);
        GitProcess.Account = context.Settings.Values.GitAccount;
        UpdateSync(dt);
    }

    // What's taken out of the caches of where a repository is, kept so a second with nothing to take makes no garbage
    private readonly List<string> forgetting = [];

    // Where a .git would be for every folder found to have no repository, its own and every folder's above it, so the
    // look every second for one having been made is a stat of each and not the walk up the disk with its paths again
    private readonly Dictionary<string, string[]> wouldBe = new(PlatformPaths.Comparer);

    /// <summary>
    /// Helper method to forget every folder and path that had no repository, for it to be looked for again. Every
    /// second only the ones where a .git turned up are, which is all of them as far as anybody can tell, and none
    /// makes any garbage while nothing turns up.
    /// </summary>
    private void ForgetNone(bool all = true)
    {
        foreach (var (key, value) in foundFor)
            if (value is null && (all || GitTurnedUp(key))) forgetting.Add(key);
        foreach (string key in forgetting)
            foundFor.Remove(key);

        // Nothing turned up anywhere, the paths that had none still have none
        bool any = forgetting.Count > 0;
        forgetting.Clear();
        if (!all && !any)
            return;

        foreach (var (key, value) in foundForPath)
            if (value is null) forgetting.Add(key);
        foreach (string key in forgetting)
            foundForPath.Remove(key);
        forgetting.Clear();
    }

    // Whether a .git is there now where there was none, in a folder that had no repository or one above it
    private bool GitTurnedUp(string folder)
    {
        if (!wouldBe.TryGetValue(folder, out string[]? places))
            return true;

        foreach (string place in places)
        {
            if (Path.Exists(place))
                return true;
        }

        return false;
    }

    // Helper method to note where a .git would be for a folder that has no repository, the same places Find looked
    private void NoteNone(string folder)
    {
        var places = new List<string>();
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
            places.Add(Path.Combine(at, ".git"));
        wouldBe[folder] = [.. places];
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
                listedDirty = true;

                // Where git was as it's found, so a commit made before the first look still counts as a move
                found.LookForMoves();
            }
        }

        if (found is null)
            NoteNone(folder);
        else
            wouldBe.Remove(folder);

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

        /// <summary>What git is in the middle of, a merge or a rebase stopped on conflicts, as of the last look.</summary>
        public GitOperation Operation { get; private set; }

        /// <summary>Whether anything is changed, staged or not or new, as of the last look (the star after the branch).</summary>
        public bool HasChanges { get; private set; }

        // Bumped every time git is asked about it, so what comes back from an older ask is let go of
        public int Generation { get; private set; }

        /// <summary>The name of its folder, what its heading and the status bar call it.</summary>
        public string Name { get; } = Path.GetFileName(git.Root);

        /// <summary>Whether it was let go of, its .git gone from the disk.</summary>
        public bool Gone { get; set; }

        private float settleIn = 0.01f;
        private bool asking;

        // The files a look stats and when each was last written. HEAD and the index are the worktree's own, the branch's
        // ref, its upstream's, FETCH_HEAD and packed-refs are where every worktree shares them (the common folder)
        private const int HEAD = 0, INDEX = 1, BRANCH = 2, UPSTREAM = 3, FETCHED = 4, PACKED = 5;
        private readonly string?[] watched = new string?[6];
        private readonly DateTime[] stamps = new DateTime[6];
        private string? commonPath, branchFor, upstreamFor;

        /// <summary>Asked again soon, or straight away.</summary>
        public void Dirty(bool now = false) => settleIn = now ? 0.01f : Math.Max(settleIn, SETTLE);

        /// <summary>
        /// HEAD, the index, the branch's own ref and the remote's, which a commit, a switch, a stage, a pull or a fetch in
        /// the terminal all touch. A fetch writes FETCH_HEAD and a push the upstream's ref, and a gc packs them into packed-refs.
        /// </summary>
        public void LookForMoves()
        {
            // Where the files are is worked out once (and the refs when the branch changes), every second it was a read of
            // the .git file and a handful of paths put together, garbage for a look that finds nothing most times
            if (commonPath is null)
            {
                string dot = DotGit();
                string common = CommonDir(dot);
                commonPath = common;
                watched[HEAD] = Path.Combine(dot, "HEAD");
                watched[INDEX] = Path.Combine(dot, "index");
                watched[FETCHED] = Path.Combine(common, "FETCH_HEAD");
                watched[PACKED] = Path.Combine(common, "packed-refs");
            }

            string? branch = Status?.Branch;
            if (!ReferenceEquals(branch, branchFor))
            {
                branchFor = branch;
                watched[BRANCH] = branch is null ? null : Path.Combine(commonPath, "refs", "heads", branch);
            }

            // origin/main is refs/remotes/origin/main
            string? upstream = Status?.Upstream;
            if (!ReferenceEquals(upstream, upstreamFor))
            {
                upstreamFor = upstream;
                watched[UPSTREAM] = upstream is null ? null : Path.Combine(commonPath, "refs", "remotes", upstream);
            }

            bool moved = false;
            for (int i = 0; i < watched.Length; i++)
            {
                DateTime stamp = watched[i] is { } file ? Stamp(file) : default;
                if (stamp != stamps[i])
                {
                    stamps[i] = stamp;
                    moved = true;
                }
            }

            if (moved)
                Dirty(now: true);
        }

        public void Update(float dt)
        {
            if (settleIn <= 0.0f || asking || (settleIn -= dt) > 0.0f)
                return;

            settleIn = 0.0f;
            asking = true;
            // What git is in the middle of is read with the status, a merge stopping or being called off moves the index too
            owner.context.Work.InBackground("git status", () =>
            {
                GitStatus? status = Git.Status();
                bool changes = status?.Files.Values.Any(state => state.Change != GitChange.Ignored && (state.Staged || state.Unstaged)) == true;
                return (Status: status, Changes: changes, Remote: Git.HasRemote(), Operation: Git.Operation());
            }, found =>
            {
                asking = false;
                if (found.Status is not { } status)
                    return;

                Status = status;
                HasChanges = found.Changes;
                HasRemote = found.Remote;
                Operation = found.Operation;
                Generation++;
                owner.FollowOperation();
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

        // Where the refs every worktree shares are, a linked worktree's folder says in its commondir file, anything else
        // has them in its own folder
        private static string CommonDir(string dot)
        {
            try
            {
                string file = Path.Combine(dot, "commondir");
                if (File.Exists(file))
                    return Path.GetFullPath(Path.Combine(dot, File.ReadAllText(file).Trim()));
            }
            catch (IOException) { }

            return dot;
        }

        // One stat, a file that isn't there has the same far off time every look (1601), so it never counts as a move
        private static DateTime Stamp(string file)
        {
            try { return File.GetLastWriteTimeUtc(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
        }
    }
}
