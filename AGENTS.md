# Agent instructions

## Release prep ("get ready to commit/push/publish")

Before committing for a release, always update `scripts/changenote.txt` so it is accurate for what
changed since the last push/publish:

- Review `git log` and the diff since the previous release commit.
- Rewrite the file as user-facing Steam Workshop BBCode (`[b]vX.Y.Z[/b]` + `[list][*]...[/list]`).
- Only include player-visible changes (fixes, features, behavior). Leave out tooling, build and publish-script changes.
- Keep it in sync with the README and `workshop/workshop_description.txt` patch notes, and the version in `Plugin.cs` / the `.csproj`.
- `scripts/Publish-Workshop.ps1` uploads this file as the Workshop change note.
