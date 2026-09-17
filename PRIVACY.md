# Privacy and local data

## Where data goes

| Feature | Data used or sent |
| --- | --- |
| Local ASR | System or microphone audio is decoded locally. |
| Cloud ASR | Audio is sent to the selected speech service. |
| Online translation | Recognized text, relevant prior context, glossary and prompts are sent to the configured service. |
| AI glossary generation | The entered domain description and generation prompt are sent to the configured AI endpoint. |
| Local translation provider | Text goes to the configured server, which may be on this machine or your network. |

API calls may cost money. Provider data retention is governed by that provider. Automatic upstream release checks are disabled in this derivative.

## Files to keep private

The working directory or configured output directory may contain:

- `setting.json` and backups: endpoints, API keys entered directly, model paths and preferences.
- `glossary.txt` and backups: domain vocabulary, possibly confidential project names.
- `translation_history.db` and SQLite sidecars: source and translated text.
- `transcripts/`, JSONL/TXT/Markdown exports and `asr_debug.log`: speech text, timestamps and diagnostic paths.
- Pending recovery/spool files: audio samples and text retained to recover processing.
- `.env` files and local test artifacts: credentials or user-specific recordings/configuration.

Do not upload the working folder or attach whole logs to an Issue. Use a synthetic reproduction; remove names, paths, text content and credentials before sharing screenshots. `.gitignore` protects normal staging only: it does not remove already committed secrets or prevent a force-add.

The app does not provide encrypted storage for configuration or history. Prefer environment-variable references such as `${DEEPSEEK_API_KEY}` when avoiding a literal key in the JSON file. The app does not load `.env` automatically. Backups and old files may still contain values entered previously.

Stopping transcription does not delete existing data. Manage retained files explicitly after closing the application. A recovered audio backlog may exist even if exported transcripts are disabled.

## Public packages

`prepare-open-source.ps1` exports only an explicit file allowlist, excludes Git history and scans the exported tree. `package-portable.ps1` starts from a new build folder and uses public templates instead of the current user's configuration. Neither package includes recognition weights.

Automated secret checks reduce accidental exposure; they cannot determine every form of confidential content. Review the public tree and release asset list before publishing.
