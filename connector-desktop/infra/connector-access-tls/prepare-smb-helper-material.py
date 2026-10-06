"""Create isolated, encrypted loopback TLS material in a pre-protected directory."""
import argparse
import base64
import ipaddress
import json
import os
import secrets
import subprocess
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
root = args.output
if not root.is_absolute() or not root.is_dir() or any(root.iterdir()) or root.is_symlink():
    raise SystemExit('An empty pre-protected absolute output directory is required.')
if os.name != 'nt':
    raise SystemExit('This operator requires Windows ACL verification.')
# Verify before generating keys or writing any private bytes. The path is data,
# passed in a task-specific environment variable rather than interpolated code.
acl_probe = r'''
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
try {
 $p=$env:TASK_SMB_MATERIAL_OUTPUT;$currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $trusted=@($currentSid,'S-1-5-18','S-1-5-32-544')|Select-Object -Unique
 $full=[IO.Path]::GetFullPath($p);$cursor=[IO.Path]::GetPathRoot($full)
 $owners=@($trusted)+@('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
 $replaceMask=[int64]0x10000000 -bor [int64]0x40000000 -bor [int64]0x000D0040
 foreach($part in @('')+@($full.Substring($cursor.Length).Split('\'))) {
  if($part){$cursor=Join-Path $cursor $part}
  if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){exit 2}
  $parentAcl=Get-Acl -LiteralPath $cursor
  if($parentAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $owners){exit 2}
  $raw=[Security.AccessControl.RawSecurityDescriptor]::new($parentAcl.GetSecurityDescriptorBinaryForm(),0)
  if($null -eq $raw.DiscretionaryAcl){exit 2}
  foreach($r in $parentAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
   if($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly){continue}
   if($r.AccessControlType -eq 'Allow' -and $r.IdentityReference.Value -notin $owners -and ([int64]$r.FileSystemRights -band $replaceMask)){exit 2}
  }
 }
 $a=Get-Acl -LiteralPath $full;$rules=@($a.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
 if($a.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $currentSid -or !$a.AreAccessRulesProtected -or $rules.Count -ne $trusted.Count){exit 2}
 foreach($sid in $trusted){if(@($rules|Where-Object{$_.IdentityReference.Value -ceq $sid -and $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights -eq 'FullControl' -and !$_.IsInherited}).Count -ne 1){exit 2}}
 exit 0
} catch {exit 2}
'''
env = os.environ.copy()
env['TASK_SMB_MATERIAL_OUTPUT'] = str(root)
# A PowerShell 7 caller's module path is incompatible with Windows PowerShell.
# Use only the operating system's module directory for this ACL-only child.
windows = Path(os.environ['SystemRoot'])
shell_root = windows / 'System32/WindowsPowerShell/v1.0'
env['PSModulePath'] = str(shell_root / 'Modules')
probe = subprocess.run([str(shell_root / 'powershell.exe'), '-NoProfile', '-NonInteractive', '-EncodedCommand',
                        base64.b64encode(acl_probe.encode('utf-16le')).decode('ascii')],
                       env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15)
if probe.returncode != 0:
    raise SystemExit('Output directory ACL or plain-path verification failed; no private material was generated.')
key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
subject = x509.Name([x509.NameAttribute(NameOID.ORGANIZATION_NAME, 'Structura'),
                     x509.NameAttribute(NameOID.COMMON_NAME, 'Connector SMB loopback service')])
now = datetime.now(timezone.utc)
cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject)
        .public_key(key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(minutes=5)).not_valid_after(now + timedelta(days=365))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=False,
                       key_encipherment=True, data_encipherment=False, key_agreement=False,
                       key_cert_sign=False, crl_sign=False, encipher_only=False, decipher_only=False), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address('127.0.0.1'))]), critical=False)
        .sign(key, hashes.SHA256()))
password = secrets.token_urlsafe(48)
pfx = pkcs12.serialize_key_and_certificates(b'connector-smb-loopback', key, cert, None,
                                          serialization.BestAvailableEncryption(password.encode('ascii')))
loaded_key, loaded_cert, chain = pkcs12.load_key_and_certificates(pfx, password.encode('ascii'))
if loaded_key is None or loaded_cert.fingerprint(hashes.SHA256()) != cert.fingerprint(hashes.SHA256()) or chain:
    raise SystemExit('Encrypted PKCS12 readback failed.')
with (root / 'smb-helper-server.pfx').open('xb') as stream:
    stream.write(pfx)
with (root / 'smb-helper-secrets.json').open('x', encoding='utf-8') as stream:
    json.dump({'CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD': password}, stream)
metadata = {'format': 'structura.smb.loopback-tls.v1', 'createdUtc': now.isoformat(),
            'certificateSha256': cert.fingerprint(hashes.SHA256()).hex(), 'rsaBits': key.key_size,
            'notAfterUtc': cert.not_valid_after_utc.isoformat(), 'san': ['127.0.0.1'],
            'enhancedKeyUsage': 'serverAuth', 'trustedRootInstalled': False,
            'trustMode': 'explicit leaf pin on a literal loopback origin',
            'renewal': 'Replace certificate and its configured backend pin before notAfterUtc.'}
with (root / 'public-metadata.json').open('x', encoding='utf-8') as stream:
    json.dump(metadata, stream, indent=2)
print(json.dumps({'generated': True, 'certificateSha256': metadata['certificateSha256'],
                  'notAfterUtc': metadata['notAfterUtc'], 'trustedRootInstalled': False}))
