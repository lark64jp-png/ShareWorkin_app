# Builds

`repo/builds/` is the managed output area for installer builds and related release notes.

Track small management files here, such as:

- `README.md`
- `*SHA256*.txt`
- short release notes in Markdown

Do not track large artifacts here. Installer `.exe`, package `.zip`, runtime installer files, logs, and temporary output stay ignored by `.gitignore`.

Operational rule:

- Keep Git/GitHub focused on reproducible assets such as source, settings, and lightweight management notes.
- Keep release artifacts out of commits. Tracking installers or packages would require a separate artifact-sync workflow and additional review responsibility.
