"""Fail closed on unexpected public files; report locations, never matched values."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT_FILES = {
    '.env.example', '.gitattributes', '.gitignore', 'LICENSE', 'NOTICE',
    'README.md', 'README_zh-CN.md', 'README_Portability.md', 'PRIVACY.md',
    'SECURITY.md', 'CONTRIBUTING.md', 'CHANGES.md', 'OPEN_SOURCE_RELEASE.md',
    'THIRD_PARTY_NOTICES.md', 'LiveCaptionsTranslator.csproj',
    'LiveCaptionsTranslator.sln', 'asr-terms.json', 'glossary.example.txt',
    'setting.portable.example.json', 'package-portable.ps1',
    'prepare-open-source.ps1', 'public-source.json',
}

def allowed(path):
    parts = path.parts
    if len(parts) == 1:
        return path.name in ROOT_FILES
    if any(p in {'.git', 'bin', 'obj', 'artifacts', '__pycache__'} for p in parts):
        return False
    top, suffix = parts[0], path.suffix.lower()
    if top == 'src':
        return suffix in {'.cs', '.xaml'} or path.as_posix() == 'src/LiveCaptions-Translator.ico'
    if top == 'tests':
        return len(parts) == 2 and suffix in {'.cs', '.csproj'}
    if top == 'Properties':
        return len(parts) == 2 and suffix in {'.cs', '.tt'}
    if top == 'scripts':
        return len(parts) == 2 and suffix in {'.py', '.ps1'}
    if top == '.github':
        return len(parts) == 3 and parts[1] in {'workflows', 'ISSUE_TEMPLATE'} and suffix in {'.yml', '.yaml'}
    if top == 'licenses':
        return suffix in {'.txt', '.md'}
    return path.as_posix() == 'models/README.md'

PATTERNS = {
    'absolute-user-path': re.compile(r'(?i)[a-z]:[\\/]+(?:users|trans_bot|codexdata)[\\/]+'),
    'personal-video-tracking': re.compile(r'(?i)(?:vd_source|trackid|spm_id_from)='),
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'credential-url': re.compile(r'https?://[^\s/<>:@]+:[^\s/<>@]+@'),
    'known-key-format': re.compile(r'(?:AIza[\w-]{35}|gh[pousr]_[A-Za-z0-9]{30,}|sk-[A-Za-z0-9]{24,})'),
}

def private_values(root):
    values = set()
    def collect(value, key=''):
        if isinstance(value, dict):
            for k, v in value.items():
                collect(v, k)
        elif isinstance(value, list):
            for v in value:
                collect(v, key)
        elif isinstance(value, str) and re.search(r'(?i)(key|secret|token|appid|password)$', key):
            if len(value) >= 6 and not re.search(r'(?i)(\$\{|your_|placeholder|test-only)', value):
                values.add(value)
    for path in (root / 'setting.json', root / 'setting.json.bak'):
        if path.is_file():
            try:
                collect(json.loads(path.read_text(encoding='utf-8-sig')))
            except (ValueError, OSError):
                raise RuntimeError('Cannot inspect a local config for exact-value comparison') from None
    env_path = root / '.env'
    if env_path.is_file():
        for line in env_path.read_text(encoding='utf-8-sig').splitlines():
            if '=' in line and not line.lstrip().startswith('#'):
                key, value = line.split('=', 1)
                collect(value.strip().strip('\"\''), key.strip())
    for key, value in os.environ.items():
        if re.search(r'(?i)(API_KEY|API_SECRET|ACCESS_TOKEN)$', key):
            collect(value, key)
    # Compare local identity silently; preserve unrelated upstream attribution.
    for key in ('user.name', 'user.email'):
        result = subprocess.run(['git', '-C', str(root), 'config', '--get', key], capture_output=True, text=True)
        value = result.stdout.strip()
        if len(value) >= 5 and '@users.noreply.github.com' not in value:
            values.add(value)
    host = os.environ.get('COMPUTERNAME', '')
    if len(host) >= 5:
        values.add(host)
    return values

def scan(root, exact=()):
    findings = []
    count = 0
    manifest = json.loads((root / 'public-source.json').read_text(encoding='utf-8-sig'))
    declared = set(manifest['files'])
    if len(declared) != len(manifest['files']):
        findings.append({'rule': 'duplicate-manifest-entry', 'file': 'public-source.json'})
    for path in root.rglob('*'):
        if not path.is_file():
            continue
        rel = path.relative_to(root)
        if rel.parts[0] in {'.git', 'artifacts', 'bin', 'obj'} or 'obj' in rel.parts or 'bin' in rel.parts:
            # Generated files may exist after validation; never export them.
            continue
        name = rel.as_posix()
        count += 1
        if path.is_symlink() or not allowed(rel) or name not in declared:
            findings.append({'rule': 'unexpected-file', 'file': name})
        raw = path.read_bytes()
        text = raw.decode('utf-8', errors='replace')
        for rule, regex in PATTERNS.items():
            for match in regex.finditer(text):
                findings.append({'rule': rule, 'file': name, 'line': text[:match.start()].count('\n') + 1})
        if any(v.encode('utf-8') in raw or v.encode('utf-16-le') in raw for v in exact):
            findings.append({'rule': 'local-private-value', 'file': name})
    for name in declared:
        relative = Path(name)
        if relative.is_absolute() or '..' in relative.parts or not allowed(relative):
            findings.append({'rule': 'invalid-manifest-path', 'file': name})
        elif not (root / relative).is_file():
            findings.append({'rule': 'missing-public-file', 'file': name})
    return {'files_checked': count, 'finding_count': len(findings), 'findings': findings}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--private-root', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    result = scan(args.root.resolve(), private_values(args.private_root.resolve()) if args.private_root else ())
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 1 if result['finding_count'] else 0

if __name__ == '__main__':
    sys.exit(main())
