"""Privacy checks must reject unsafe content without echoing it."""
import importlib.util
import json
from pathlib import Path
import shutil
import uuid
import sys

sys.dont_write_bytecode = True

spec = importlib.util.spec_from_file_location('public_check', Path(__file__).with_name('check-public-source.py'))
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)

test_area = Path(__file__).resolve().parent.parent / 'artifacts' / 'privacy-tests'
test_area.mkdir(parents=True, exist_ok=True)
def run(root):
    manifest = {'files': ['public-source.json', 'README.md']}
    (root / 'public-source.json').write_text(json.dumps(manifest), encoding='utf-8')
    readme = root / 'README.md'
    readme.write_text('Synthetic public documentation.', encoding='utf-8')
    assert checker.scan(root)['finding_count'] == 0
    probes = [
        ('credential', 'sk-' + 'A' * 32, 'known-key-format'),
        ('profile path', 'C:' + '/' + 'Users/' + 'example/Documents', 'absolute-user-path'),
        ('private value', 'synthetic-local-secret-value', 'local-private-value'),
    ]
    for _, value, rule in probes:
        readme.write_text(value, encoding='utf-8')
        result = checker.scan(root, ['synthetic-local-secret-value'])
        assert any(f['rule'] == rule for f in result['findings'])
        assert value not in json.dumps(result)
    readme.write_text('Safe.', encoding='utf-8')
    (root / 'setting.json').write_text('{}', encoding='utf-8')
    assert any(f['rule'] == 'unexpected-file' for f in checker.scan(root)['findings'])
    (root / 'setting.json').unlink()
    manifest['files'].append('../outside.txt')
    (root / 'public-source.json').write_text(json.dumps(manifest), encoding='utf-8')
    assert any(f['rule'] == 'invalid-manifest-path' for f in checker.scan(root)['findings'])
root = test_area / uuid.uuid4().hex
root.mkdir()
try:
    run(root)
finally:
    assert root.resolve().is_relative_to(test_area.resolve())
    shutil.rmtree(root)
print('PASS privacy scanner: safe tree, key, path, exact private value, unexpected file, traversal and redacted output')
