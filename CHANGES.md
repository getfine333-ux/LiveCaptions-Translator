# Changes from upstream

This derivative is based on SakiRinn/LiveCaptions-Translator at `794f442`.

- Added system/microphone audio capture, local sherpa-onnx recognition and optional cloud ASR adapters.
- Added audio queues, semantic boundaries, source revisions and recovery to handle continuous speech.
- Added concurrent translation with ordered commits, retry handling and bilingual validation.
- Added stable caption reading pages, protected dwell time and newest-first history.
- Added contextual terminology correction and editable, AI-generated glossary drafts.
- Changed settings to draft edits with explicit Save and apply.
- Added offline regression tests using generated inputs and mock API responses.
- Prepared Windows x64 directory-based portable builds, public documentation and dependency notices.
- Disabled upstream automatic update checks; upstream URLs remain for attribution and legacy documentation.
- Removed the embedded upstream browser-extension credential. Legacy Google2 settings now use the existing Google path.
- Excluded personal configuration, credentials, recordings, model weights, history, development notes and old Git metadata from the public source export.

These changes do not guarantee perfect transcription or constant latency. The source export intentionally contains no original Git history; upstream provenance is preserved here and in NOTICE.
