# SETUP NOTES

## Access
- Host: 62.113.36.107
- SSH: 22
- RDP: 3389
- User: opwork_admin
- Password: current (kept private, not stored in plaintext here)

## Current status
- OpenSSH Server installed
- `sshd` running
- `TermService` running
- 22/tcp listening
- 3389/tcp listening

## Targets
- Tekla MultiUser endpoint: 62.113.36.107:1238
- Revit Server: Host mode
- Storage: `D:\BIM_Models`
- SMB share: `\\62.113.36.107\BIM_Models`

## Security notes
- Restrict firewall by trusted public IPs
- Do not expose SMB 445 to all internet
- Rotate temporary passwords after setup
