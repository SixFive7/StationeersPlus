# Inspector Plus TODO

## Release posture decision

- [ ] **Decide: public Workshop mod, or local developer-only tool?** The current state is provisionally public-Workshop-conforming (full README, template-shaped About.xml with Features / Compatibility / Reporting Issues / License sections, Preview image placeholder files, Apache 2.0 LICENSE + NOTICE). This was the direction chosen when the template was rolled out. If it should instead stay strictly local:
  - Delete `README.md` and `RESEARCH.md` (or keep them as internal docs with the "not for end users" framing already in them)
  - Delete the three preview placeholder files: `Preview.source.png.placeholder`, `About/Preview.png.placeholder`, `About/thumb.png.placeholder`
  - Collapse `About.xml` back to a short plain-text `<Description>` that makes the dev-tool framing obvious and drop the `<ChangeLog>`, `<ModID>` (optional), and `Reporting Issues` / `License` h2 sections from the Workshop-facing surface
  - Remove the `BepInEx` and `StationeersLaunchPad` tags, keep only the `Tool` tag
  - Skip the GitHub repo creation and Workshop publish

  If it stays public, the remaining public-release steps apply:

- [ ] Generate preview art (`Preview.source.png` at repo root, `About/Preview.png` at 1280x720, `About/thumb.png` at 640x360). Placeholders document the required dimensions.
- [ ] Create GitHub repo `SixFive7/InspectorPlus` and push
- [ ] Run `gh repo edit SixFive7/InspectorPlus --description "Developer tool that dumps live Stationeers runtime state to JSON on demand for mod development."`
- [ ] Publish to Steam Workshop, populate `<WorkshopHandle>` in `About.xml`, replace the "Once the mod is published..." sentence in the README Changelog section with the real Workshop change-notes URL
