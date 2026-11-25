# Held changes in Hex Link

Hold a file while you work on changes that need to reach the other devices together. Hex Link keeps sharing the last shared version while you edit and save the held copy. You can see held files from the file tree, the editor tab, or the shared folder in Devices.

## Hold and submit

1. Open a file in a shared folder and choose **Hold changes** in its tab or file tree menu. The **Hex Link: Hold changes to all open files** command holds open files across your shared folders. **Hex Link: Hold all uncommitted changes** holds the files changed against HEAD in the current shared folder's Git repositories.
2. Edit and save normally. Leave related files held until the set is ready. If another device changes a held file, Hex keeps its latest incoming version and shows a notice with **Update from others** and **Show**.
3. Choose **Submit held changes**. Review the per-file outcome. The configured pre-publish check runs by default, and **Submit anyway** skips it. A failure leaves the files held and gives you the check output.
4. If the **Merge editor** opens, compare **Mine**, **Theirs**, and **Base**, then resolve the conflicts in **Result**. Use yours, use theirs, use both, or edit the result directly. **Save merged file** is available once every conflict is resolved. For a batch, Hex releases its holds after every required merge is complete. If the check fails then, **Submit anyway** uses your existing resolutions.
5. Choose **Update from others** to merge the latest incoming version while keeping the file held, or **Discard held changes** to restore the shared version and release the hold. Discard also handles incoming deletions. Hex keeps a local backup of discarded files for seven days and reports the backup directory.

The **Merge editor** also resolves Git conflicts. Open **Merge conflicts** in Source Control and choose **Resolve in merge editor**, or use **Open merge editor** in the file's conflict bar. Completing a Git merge editor session stages the file. Use Git's continue action when all conflicts are resolved. For text files with a configured language server, Result uses an isolated language session in the original project. Binary conflicts offer the two original versions.

## Scope and limits

- Holds belong to this device. Another device, including one you own, can keep editing its own copy. A rename is treated as a deletion at the old path plus an addition at the new path.
- Hold related files together. Other devices can see an incomplete set during development if a required file is still held. The batch releases together on the holder, and peers receive the individual files through normal sync.
- Link uses the last synced content as the basis when it can recover it. If that content is unavailable, it uses the local copy at the moment of the hold. The `hex_link_hold list` response reports the source as `agreed`, `git`, or `local`.
- Held working changes stay out of Link file sync. A linked device with read rights can still see working text through other authorized tools, including search, Git diff, and document tools. Remote `hex_read` shows the last shared version with a held notice. Use device access rights to control who can inspect your workspace.
- Git publication of a repository containing held files waits until those files are released. Link only projects the shared version over its own file routes. Commands or applications that access the working directory directly can read the local file.

## MCP

Call `hex_link_hold` on the device that owns the hold. Pass `action` as `hold`, `list`, `plan`, `submit`, `update`, or `discard`. Pass the workspace folder name in `folder` and file paths in `paths`. For example, either `["src/main.cs"]` or `["work/src/main.cs"]` works for `folder: "work"`. Omitting `paths` selects all held files for that folder, except that `hold` requires paths.

Pass `partial: true` to submit clean files while leaving unresolved ones held. Pass `check: false` to skip the configured check. Submit and update return `needs_merge_editor` with each file and its conflict count so a person can open the editor. Linked callers manage holds from the device that owns them.
