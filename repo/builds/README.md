# Builds

`repo/builds/` is the managed output area for installer builds and related release notes.

Track small management files here, such as:

- `README.md`
- `*SHA256*.txt`
- short release notes in Markdown

Do not track large artifacts here. Installer `.exe`, package `.zip`, runtime installer files, logs, and temporary output stay ignored by `.gitignore`.
