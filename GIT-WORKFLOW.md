# PLC program in git, from Git Bash

Keep a TIA Portal project's program in a plain git repository, edit it in VS Code, and use TIA
Portal only to compile. Everything below runs in **Git Bash** against a project you already have
open in TIA Portal.

The loop:

1. **export** every block and tag table from TIA into the repo, commit, push
2. **edit** the files in VS Code
3. **sync** the files git says changed back into TIA, and compile
4. **commit** and push, only if the compile was clean

`tools/gittest.ps1` checked this on V20: exports are byte-stable, TIA gives back exactly what was
edited, broken SCL fails the compile with the block named, and a project rebuilt from the repo
exports to the same files.

## What you need

- tia-cli installed (`tia --version` works in Git Bash), and git
- The project open in TIA Portal, and **only one** TIA Portal running
- No other `tia session` running (`tia session status`)

## 1. Connect to the open TIA Portal

```bash
tia session start
```

With one portal running, the session joins it and works on whatever project it has open. Your
window stays yours: `tia session stop` at the end leaves it open.

```bash
tia project info
```
```bash
tia devices
```

Take the station name from `devices` (for example `PLC_1`), and choose where the repository lives:

```bash
DEV=PLC_1
REPO=~/plc/Line
```

## 2. Load the helpers

Paste this once into the shell, or save it as `~/tia-git.sh` and run `source ~/tia-git.sh` in each
new shell. Run them from inside the repository.

```bash
# The named fields of each object in a tia --json array, one object per line, tab separated.
tia_rows() {
  awk -v keys="$*" '
    BEGIN { n = split(keys, k, " ") }
    { sub(/\r$/, "") }
    /^  \{/ { split("", v); next }
    /^  \}/ { row = ""; for (i = 1; i <= n; i++) row = row (i > 1 ? "\t" : "") v[k[i]]; print row; next }
    /^    "/ {
      line = $0; sub(/^    "/, "", line)
      key = line; sub(/".*/, "", key)
      val = line; sub(/^[^"]*": /, "", val); sub(/,$/, "", val)
      if (val ~ /^"/) { sub(/^"/, "", val); sub(/"$/, "", val) }
      v[key] = val
    }'
}

# TIA -> repo. Rewrites program/ and tags/ completely, so a block that is gone from the project
# shows up in git as a deleted file. Asks TIA first, so a failed connection deletes nothing.
# Returns non-zero if any block or table could not be exported.
tia_export() {
  local blocks tables failed=0
  blocks=$(tia blocks "$DEV" --json < /dev/null) || return 1
  tables=$(tia tables "$DEV" --json < /dev/null) || return 1
  rm -rf program tags && mkdir -p program tags
  while IFS=$'\t' read -r name path lang protected; do
    folder=${path#Program blocks}; folder=${folder#/}
    ref=${folder:+$folder/}$name
    dir=program${folder:+/$folder}
    mkdir -p "$dir"
    if [ "$protected" = true ]; then echo "skipped $ref: know-how protected"; continue; fi
    case $lang in
      DB|F_DB)   tia block source "$DEV" "$ref" --out "$dir/$name.db"  --quiet ;;
      STL|F_STL) tia block source "$DEV" "$ref" --out "$dir/$name.awl" --quiet ;;
      SCL|F_SCL) tia block source "$DEV" "$ref" --out "$dir/$name.scl" --quiet ;;
      *)         tia block export "$DEV" "$ref" --out "$dir/$name.xml" --quiet ;;
    esac < /dev/null || failed=1
  done < <(printf '%s\n' "$blocks" | tia_rows name path language isKnowHowProtected)
  while IFS=$'\t' read -r table; do
    tia tags "$DEV" --table "$table" --json < /dev/null > "tags/$table.json" || failed=1
  done < <(printf '%s\n' "$tables" | tia_rows name)
  return $failed
}

# Repo -> TIA, then compile. With no arguments it takes what git says changed under program/;
# give file paths to push those instead. Returns non-zero if any import or the compile failed.
tia_sync() {
  local failed=0 changes
  if [ $# -gt 0 ]; then
    changes=$(printf ' M %s\n' "$@")
  else
    changes=$(git -c core.quotePath=false status --porcelain --untracked-files=all -- program)
  fi
  [ -z "$changes" ] && echo "nothing changed under program/"
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    state=${line:0:2}; file=${line:3}; file=${file#\"}; file=${file%\"}
    rel=${file#program/}; name=${rel##*/}; base=${name%.*}; ext=${name##*.}
    folder=${rel%/*}; [ "$folder" = "$rel" ] && folder=
    ref=${folder:+$folder/}$base
    echo "== $state $file"
    case $state in
      *D*) tia block delete "$DEV" "$ref" --force ;;
      *)   case $ext in
             xml) tia block import "$DEV" "$file" --overwrite ${folder:+--folder "$folder"} ;;
             *)   tia source add "$DEV" "$name" --file "$file" ${folder:+--folder "$folder"} ;;
           esac ;;
    esac < /dev/null || failed=1
  done <<< "$changes"
  echo "== compile"
  tia compile "$DEV" < /dev/null || failed=1
  return $failed
}
```

## 3. Export the project and push the first commit

```bash
mkdir -p "$REPO" && cd "$REPO" && git init
```

Keep the files byte for byte as TIA writes them, so git never reports line-ending changes:

```bash
echo '* -text' > .gitattributes
```
```bash
tia_export
```
```bash
git status
```

You should see `program/` (one file per block, in the project's folders) and `tags/` (one JSON per
tag table). Commit it:

```bash
git add -A && git commit -m "export from TIA Portal"
```

Push it. `origin` can be any git remote; a bare repository on a share works:

```bash
git init --bare /c/git/Line.git
```
```bash
git remote add origin /c/git/Line.git
```
```bash
git push -u origin HEAD
```

## 4. Edit in VS Code

```bash
code .
```

Change `.scl`, `.db` and `.awl` files as text. A `.xml` file is a LAD/FBD block, so edit those in
TIA Portal and run `tia_export` afterwards. Save, then look at what you changed:

```bash
git status
```
```bash
git diff
```

## 5. Import the changes and compile

```bash
tia_sync
```

It imports only the files git lists as changed, then compiles the station. A compile error names
the block and the line, for example `Program blocks/Twice (FC1)/2  Expression expected.`. Fix the
file and run `tia_sync` again; it is safe to repeat.

To drop an edit instead, put the file back and push that version into TIA:

```bash
git checkout -- program/Twice.scl && tia_sync program/Twice.scl
```

## 6. Commit and push, only when it compiled

```bash
tia_sync && git add -A && git commit -m "Motor stops on stop" && git push
```

The `&&` chain is the rule: if the import or the compile fails, nothing is committed.

Optionally, check that TIA now holds exactly what you committed. Nothing should be listed:

```bash
tia_export && git status --porcelain
```

## 7. Save and disconnect

The changes are in the open project, but not on disk until you save:

```bash
tia project save
```
```bash
tia session stop
```

## Good to know

- **Pull from TIA before you edit.** If anyone changed blocks in TIA Portal, run `tia_export` and
  commit that first, or your next `tia_sync` overwrites their change.
- **Tags are exported, not synced.** `tags/*.json` records them, but change tags in TIA (or with
  `tia tag add`) and re-export. Tag comments are not carried by `tia tag add`.
- **Not in the repository:** hardware configuration (keep the project or `tia project archive`),
  PLC data types (UDTs, no export verb yet), and know-how protected blocks.
- **`source add` leaves an entry under "External source files"** for each synced file. It is
  harmless; `tia source delete "$DEV" Motor.scl --force` removes one, and the blocks stay.
- **A broken file still lands in the project.** TIA generates what it can and the compile catches
  the rest, so the project holds the broken block until you fix the file or check it out and sync.
- **ASCII block and folder names only.** `--json` escapes other characters (`ö` becomes `ö`),
  which these helpers do not decode.
- DB start values come back as assignments after `BEGIN` (`speed := 100;`), not in the declaration.
  Edit them there.
