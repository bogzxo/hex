# Discord Rich Presence

Hex can show what you are doing on Discord ("Editing Program.cs, in hex"). It talks to the Discord desktop app on your
own machine over its local IPC (a named pipe `discord-ipc-0..9` on Windows, a unix socket of the same name in
`$XDG_RUNTIME_DIR`, `$TMPDIR`, `/tmp` and the snap, flatpak and Vesktop folders elsewhere). There is no SDK and nothing
goes to any server of Hex's. It runs on a background task, tries again quietly while Discord isn't running and sends at
most one update every 15 seconds.

## Turn it on

Settings > Editor > Discord > **Discord presence** (`discord_presence`, off by default). Only you can switch it on: a
folder's `.hex/settings.hor` can't, and agents can't set it through `hex_settings_set`.

## What is shared

- `Editing <file name>`, `Browsing files`, `In the terminal` or `Idle` (after `discord_idle_minutes` of no input, 10 by
  default, 0 for never).
- `in <workspace name>` and the time since the workspace was opened.
- A picture for the file's language (C#, TypeScript, Python, Markdown, ...) and Hex's badge with its version.
- A **Get Hex** button to https://bogzxo.github.io/hex/.

Never shared: paths, file contents, folder names above the workspace's own. A file's name is replaced by "Editing a file"
(and its language is left out) when git ignores it, when it is in a private Hex Link worktree, or when it matches one of
`discord_hide_globs` (default `**/.env*`, `**/secrets/**`, keys and certificates).

| Setting | Default | |
| --- | --- | --- |
| `discord_presence` | off | The switch. |
| `discord_show_file` | on | Off says "Editing a file". |
| `discord_show_workspace` | on | Off leaves out "in <workspace>". |
| `discord_show_language_icon` | on | Off uses Hex's picture. |
| `discord_idle_minutes` | 10 | 0 never says Idle. |
| `discord_hide_globs` | `**/.env*`, `**/secrets/**`, ... | Files never named. |

A folder's `.hex/settings.hor` may set `discord_show_file`, `discord_show_workspace` and `discord_show_language_icon` to
`false` (to share less in that folder), never to `true`.

## Setting up the application id (whoever builds Hex)

Discord needs an application to show presence under.

1. Create one at https://discord.com/developers/applications. Its name is what Discord shows after "Playing", use **Hex**.
2. Copy its **Application ID** into `HexDiscord.ApplicationId` (`Hex.Core/Discord/DiscordProtocol.cs`). Or, to try a
   different application, set `HEX_DISCORD_APP_ID` in the environment, which wins over the constant.
3. Under Rich Presence > Art Assets upload square pictures (at least 512x512), named exactly:
   - `hex`, the badge and the fallback picture;
   - one per language key: `cs`, `ts`, `js`, `py`, `md`, `rs`, `go`, `java`, `kt`, `c`, `cpp`, `json`, `html`, `css`,
     `xml`, `yaml`, `sh`, `ps1`, `lua`, `swift`, `rb`, `php`, `sql`, `hor`, `glsl`, `toml` (the list is
     `DiscordLanguages`; a missing one shows no picture on Discord, it does nothing worse).

With no application id (the default) the setting says it isn't configured and nothing is connected to.
