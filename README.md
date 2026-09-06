# rigstack-terminal-tomlm

A **stackspace**: one git repository that temporarily fuses three separate projects so you
can change all of them together, then send each change back to its own project as a normal
pull request.

The three projects only make sense together:

| Directory | Project | What it is |
|---|---|---|
| `porta-pty/` | [tomlm/Porta.Pty](https://github.com/tomlm/Porta.Pty) | the pseudoterminal — talks to the OS shell |
| `xterm-net/` | [tomlm/XTerm.NET](https://github.com/tomlm/XTerm.NET) | the terminal emulator core — parses escape sequences |
| `iciclecreek-terminal/` | [tomlm/Iciclecreek.Avalonia.Terminal](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal) | the Avalonia control built on the other two |

## Why this exists

Normally these three consume each other as NuGet packages. A one-line fix in the pty means:
publish a package, wait, bump the emulator, publish that, bump the control — and you cannot
test the whole chain until it is all released.

In a stackspace all three are **source in one tree**. One commit can touch all three, the
build compiles against sources rather than packages, and each project still leaves as its
own pull request. Nothing about the individual projects changes — a plain clone of any one
of them still builds from packages exactly as before.

## What is actually stored here

**Only three files.** The projects are *not* committed to this repository — storing them
would duplicate three upstream histories that already live on GitHub. What is here is the
recipe:

- `rig.stack.jsonc` — which projects, where they come from, and which commit each is pinned at
- `Directory.Build.targets` — generated; redirects package references to the fused sources
- `README.md` — this file

`rig stack init` reads the recipe and reconstitutes the full tree. That is why a fresh clone
is ~6 MB and the working tree is ~14 MB.

## First-time setup

**1. Install rig** (a single static binary, no runtime dependencies):

```sh
curl -fsSL https://rigsmith.sh | sh
```

**2. Get the workspace:**

```sh
git clone https://github.com/JohnCampionJr/rigstack-terminal-tomlm.git
cd rigstack-terminal-tomlm
rig stack doctor --fix     # installs josh, the engine that does the fusing
rig stack init             # fetches all three projects and fuses them
```

You now have `porta-pty/`, `xterm-net/` and `iciclecreek-terminal/` as real source, and

```sh
rig stack status
```

prints each project, the commit it sits at, and whether upstream has moved.

## Working in it

Build and test as one tree:

```sh
rig build                  # or: dotnet build
rig test
```

**Commit across projects freely.** A single commit touching `porta-pty/` and
`xterm-net/` together is the entire point — do not split it by project. Splitting happens
automatically when you send the work out.

**Take new upstream work** when `rig stack status` says a project has moved:

```sh
rig stack pull xterm-net   # one project
rig stack pull             # all of them
```

## Sending work back

Each project leaves on its own. `propose` extracts just that project's changes, roots them
on its upstream's current tip, and pushes a branch to your fork with the files at their real
paths — no trace of the stackspace:

```sh
rig stack propose xterm-net wide-glyph-fix -m "Measure a wide glyph once"
```

That creates `stack/wide-glyph-fix` on `JohnCampionJr/XTerm.NET`. Open the pull request to
`tomlm/XTerm.NET` from there. Pass the **short** name — rig prepends `stack/` itself.

A workspace commit touching two projects becomes two `propose` calls, one each. Sending to
the same branch again **updates** it, so review feedback is a commit plus another `propose`.

If `propose` refuses because upstream has moved, run `rig stack pull <project>` and send
again. Do not work around it — rooting stale files on a newer tip produces a pull request
that silently reverts whatever landed in between.

## Two rules

**Never `git push` from this workspace**, and never add a remote pointing at one of the
three projects. The tree holds three rewritten histories fused together; the only sanctioned
way out is `rig stack propose`.

**Keep the working tree clean** before `init`, `pull` or `propose`. They refuse a dirty tree,
because importing stages everything and would swallow unrelated edits.

## Changing the recipe itself

This repository holds only the root files, and the fused workspace on your machine has a
different history from it — reconstituting adds the import commits locally. So edit the
recipe in a **plain clone**, not in a working stackspace:

```sh
git clone https://github.com/JohnCampionJr/rigstack-terminal-tomlm.git /tmp/seed
cd /tmp/seed
$EDITOR rig.stack.jsonc          # add a project, move a cursor
git commit -am "Track the new pty branch" && git push
```

Then re-clone the workspace, or `rig stack pull` in the one you have.

To add a project without hand-editing, `rig stack add` writes the manifest entry and imports
it in one step:

```sh
rig stack add github.com/tomlm/Some.Lib --fork github.com/JohnCampionJr/Some.Lib --as some-lib
rig stack wire                   # re-detect which references cross projects
```

Commit the resulting `rig.stack.jsonc` and `Directory.Build.targets` back to this repository —
**only** those two files.

## Reading `rig stack status`

- `up to date` — the project is at the commit the recipe pins
- `upstream moved (abc1234)` — new work exists upstream; `rig stack pull <project>`
- `unsent changes` — you have commits for that project that no `propose` has sent yet
- `cannot tell whether it has unsent changes (no import commit in this history)` — expected in
  a fresh clone before `rig stack init`, because the recipe alone carries no import commits

Full guide: <https://rigsmith.dev/rig/stack>
