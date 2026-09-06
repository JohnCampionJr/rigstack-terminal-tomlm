# rigstack-terminal-tomlm

A [`rig stack`](https://rigsmith.dev/rig/stack) workspace that fuses three projects which
only make sense together — a pseudoterminal, a terminal emulator core, and the Avalonia
control built on both — so a change spanning all three is one commit and one build, and
still leaves as an ordinary pull request to each project.

**This repository holds the workspace, not the projects.** Only `rig.stack.jsonc` and the
generated build overlay are here. The member repositories are *rebuilt* from the manifest,
never committed — storing them would duplicate three upstream histories that already live
on GitHub and ship on nuget.org.

## Rebuild it on any machine

```sh
git clone https://github.com/JohnCampionJr/rigstack-terminal-tomlm.git
cd rigstack-terminal-tomlm
rig stack doctor --fix     # installs the josh engine if missing
rig stack init             # fetches and fuses every member in the manifest
```

That reproduces `porta-pty/`, `xterm-net/` and `iciclecreek-terminal/` at the cursors
recorded in the manifest. `rig stack pull` moves them to current upstream.

## Members

| Directory | Upstream | Fork |
|---|---|---|
| `porta-pty` | [tomlm/Porta.Pty](https://github.com/tomlm/Porta.Pty) | [JohnCampionJr/Porta.Pty](https://github.com/JohnCampionJr/Porta.Pty) |
| `xterm-net` | [tomlm/XTerm.NET](https://github.com/tomlm/XTerm.NET) | [JohnCampionJr/XTerm.NET](https://github.com/JohnCampionJr/XTerm.NET) |
| `iciclecreek-terminal` | [tomlm/Iciclecreek.Avalonia.Terminal](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal) | [JohnCampionJr/Iciclecreek.Avalonia.Terminal](https://github.com/JohnCampionJr/Iciclecreek.Avalonia.Terminal) |

`rig stack wire` redirects `Porta.Pty` (consumed by the other two) and `XTerm.NET`
(consumed by the control) to these sources instead of nuget.org.
`Iciclecreek.Avalonia.Terminal` is produced here and consumed by nothing in the
workspace — its consumer is Tweed, which is not a member.
