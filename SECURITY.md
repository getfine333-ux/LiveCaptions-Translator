# Security reporting

Do not disclose API keys, private transcripts, audio or exploitable details in a public Issue.

If the repository's Security tab offers **Report a vulnerability**, use that private channel. If private reporting is unavailable, open an Issue asking for a private contact method without including sensitive details. Do not send reports for this modified fork to upstream maintainers unless the issue is confirmed to affect upstream too.

If you exposed a real API key, revoke/rotate it with the provider; deleting a file or commit does not revoke a key. Review configuration backups and logs as well.

Release candidates are experimental. No security support commitment is implied for older builds or third-party services. Maintainers should enable GitHub secret scanning/push protection and private vulnerability reporting where available.
