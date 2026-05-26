# Web

Source and build output for the public documentation site at https://stationeers.huisman.io.

## Layout

| Path | Tracked | Purpose |
|---|---|---|
| `mkdocs.yml` | yes | MkDocs configuration |
| `requirements.txt` | yes | Pinned Python dependencies for reproducible builds |
| `content/` | yes | Hand-written pages that exist only for the site (landing page, section intros) |
| `overrides/` | yes | MkDocs Material template overrides (renders the Research frontmatter metadata) |
| `_staging/` | no (gitignored) | Intermediate docs tree assembled by the build script; combines `content/` + `Research/` + `tools/*/index.html` |
| `site/` | yes | Built static site. This is what gets mirrored to the SMB share at `\\10.20.30.250\nvme-system\containers\stationeers\` |

## Build, commit, and deploy

From the repo root, after the source change has been committed (autonomous `Research:` commit or user-approved commit to other publishable source):

```powershell
# 1. Rebuild Web/site/ from Research/, tools/, and Web/content/
.\tools\publish-web\build.ps1

# 2. Commit the rebuilt site (Web/site/ only; hook-enforced)
git add Web/site/
git commit -m "Publish: <summary>"

# 3. Mirror Web/site/ to the SMB share
.\tools\publish-web\deploy.ps1
```

The commit step is the autonomous publish lane (`Publish:` prefix). Hook `site-commit-hook.ps1` enforces that only `Web/site/` paths are staged in a `Publish:` commit. See repo-root `CLAUDE.md` "Workflow: site publish commits are autonomous" for the full rules.

The SMB share is treated as a downstream mirror. Never hand-edit files there; they will be overwritten on the next deploy.

## Source layout assembled into the site

- Landing page comes from `Web/content/index.md`
- `Web/content/research/index.md` becomes the Research section overview
- `Web/content/tools/index.md` becomes the Tools section overview
- Every `.md` under `Research/` (except `INDEX.md`, `CLAUDE.md`, `WORKFLOW.md` which are agent-workflow internals) is copied into the Research section
- Every `tools/<name>/index.html` is copied into the Tools section as a standalone HTML page

## Why two folders?

`Research/` and `tools/` are the canonical sources for their content; they exist for reasons unrelated to publication (agent knowledge base; interactive utilities). `Web/` is the publication shell: it adds the landing page, navigation, theme, search, and tag rendering on top of those sources. Sources stay clean; the site adapts to them.
