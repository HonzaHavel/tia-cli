# PLC program in git, from Git Bash

Keep a TIA Portal project's program in a plain git repository, edit it in VS Code, and use TIA
Portal only to compile. Everything below runs in **Git Bash**.

The repository is a tia **workspace**: a folder holding the project in `.tia/`, which git ignores,
and the program as files in `source/`, which git keeps. Every `tia` command run inside it works on
that project and that station, so there are no pids or station names to pass around.

```
Line3/
  .tia/
    config             committed: name, TIA version, station
    .gitignore         keeps the rest of .tia/ out of git
    project/           the working project
    archive/           Line3.zap20, for blob storage
  source/
    PLC_1/
      Program blocks/  SCL, STL and DB blocks as text; LAD, FBD, GRAPH as XML
      PLC data types/  one .udt per type
      PLC tags/        one .csv per tag table
  .gitattributes
```

The loop:

1. **`tia export`** the program from TIA into `source/`, commit, push
2. **edit** the files in VS Code
3. **`tia sync`** what git says changed back into TIA - it compiles at the end
4. **commit** and push, only if the sync was clean

## What you need

- tia-cli **0.4.1 or newer** (`tia --version` works in Git Bash), and git.
- The project as a project folder (`Line3.ap20`) or an archive (`Line3.zap20`). If it is open in
  TIA Portal, close it there first: the workspace works on a copy, and edits made in the original
  afterwards would not reach the repository.

## 1. Make the workspace (once)

Keep the path short. TIA Portal refuses to open a project whose folder path is too long, and the
project lives two folders down, in `.tia/project/Line3/`:

```bash
mkdir -p ~/plc/Line3 && cd ~/plc/Line3 && git init
```

Keep the files byte for byte as TIA writes them, so git never reports line-ending changes:

```bash
echo '* -text' > .gitattributes
```

Copy the project in, and name the station to work on:

```bash
tia init --from "C:/Projects/Line3/Line3.ap20" --station PLC_1
```

An archive works the same: `--from Line3.zap20`. The original is copied, never moved. Not sure of
the station's name? Leave `--station` out, run `tia open` and `tia devices`, then set `station:`
in `.tia/config`.

## 2. Open the project

```bash
tia open
```

This starts TIA Portal with the project, waits until it is open, and returns. TIA Portal is an
ordinary window from then on: nothing else holds it, and every `tia` command in this folder finds
it by its project. Run `tia open` again at any time; it says so when the project is already open,
and opens it again after you have closed TIA Portal.

```bash
tia workspace
```

shows where everything is and which TIA Portal has the project open. If `tia` ever stops with
exit 3, nothing has this project open: run `tia open`.

## 3. Export the project and push the first commit

```bash
tia export
```

It writes every block, data type and tag table into `source/PLC_1/` and names anything it skipped -
a know-how protected block, a tag table's user constants. Then:

```bash
git status
```

You should see `.tia/config`, `.tia/.gitignore`, `.gitattributes` and `source/` - and nothing else
from `.tia/`. Commit it:

```bash
git add -A && git commit -m "export from TIA Portal"
```

Push it. `origin` can be any git remote; a bare repository on a share works:

```bash
git init --bare /c/git/Line3.git
```
```bash
git remote add origin /c/git/Line3.git
```
```bash
git push -u origin HEAD
```

## 4. Edit in VS Code

```bash
code .
```

What you can change as text:

- **`.scl`, `.awl`, `.db`** under `Program blocks/` - the code of SCL and STL blocks, and DBs.
- **`.udt`** under `PLC data types/`.
- **`.csv`** under `PLC tags/` - one row per tag: `Name`, `Data Type`, `Logical Address`,
  `Comment`, and the three `Hmi` columns as `True`/`False`. Add a row to add a tag, delete one to
  delete it. It opens in Excel too.

A `.xml` block is LAD, FBD or GRAPH: edit those in TIA Portal and run `tia export` afterwards. Save,
then look at what you changed:

```bash
git status
```
```bash
git diff
```

## 5. Bring the changes into TIA

```bash
tia sync
```

It takes what git lists as changed under `source/PLC_1/` - tag tables first, then data types, then
blocks - deletes what you deleted, and compiles the station. A compile error names the block and
the line, for example `Program blocks/Twice (FC1)/2  Expression expected.`. Fix the file and run
`tia sync` again; it is safe to repeat.

To drop an edit instead, put the file back and push that version into TIA. git sees nothing
changed after a checkout, so name the file:

```bash
git checkout -- "source/PLC_1/Program blocks/Twice.scl"
```
```bash
tia sync --file "source/PLC_1/Program blocks/Twice.scl"
```

## 6. Commit and push, only when it compiled

```bash
tia sync && git add -A && git commit -m "Motor stops on stop" && git push
```

The `&&` chain is the rule: `tia sync` exits non-zero when a file did not go in or the compile
failed, and then nothing is committed.

Optionally, check that TIA now holds exactly what you committed. Nothing should be listed:

```bash
tia export && git status --porcelain
```

## 7. Save, archive, close

The changes are in the open project, but not on disk until you save:

```bash
tia project save
```

The repository holds the program, not the project. To hand the whole project on - to blob storage,
or to a colleague - archive it into `.tia/archive/`:

```bash
tia project archive .tia/archive --overwrite
```

Then close TIA Portal's window. Nothing else needs stopping.

## Someone else's checkout

The repository carries `.tia/config` and `source/`, but not the project. To work on it elsewhere:

```bash
git clone /c/git/Line3.git ~/plc/Line3 && cd ~/plc/Line3
```

Put the archive into `.tia/archive/` (from blob storage, or from whoever made it), then:

```bash
tia open
```

finds no project in `.tia/project/`, so it retrieves the archive there and opens it. Check the
project matches the repository before editing - nothing should be listed:

```bash
tia export && git status --porcelain
```

## Good to know

- **Export before you edit.** If anyone changed the project in TIA Portal, run `tia export` and
  commit that first, or your next `tia sync` overwrites their change.
- **A tag renamed in the CSV is a new tag.** The old one is deleted and a new one created; the
  compile shows every block that still uses the old name.
- **User constants stay in TIA.** The CSV holds tags; `tia export` says which tables have
  constants, and `tia sync` leaves them as they are.
- **Comments are in the project's editing language.** Other languages are left alone.
- **Not in the repository:** hardware configuration (keep it in the archive) and know-how
  protected blocks, which `tia export` names and skips.
- **A broken file still lands in the project.** TIA generates what it can and the compile catches
  the rest, so the project holds the broken block until you fix the file and sync again.
- DB start values come back as assignments after `BEGIN` (`speed := 100;`), not in the declaration.
  Edit them there.
- **Several stations:** `tia export PLC_2` and `tia sync PLC_2` work on another one, in
  `source/PLC_2/`.
- **Working outside the workspace.** `--no-workspace` makes one command ignore it, and then
  `--attach <pid>` or `TIA_PORTAL` pick the portal as before.
