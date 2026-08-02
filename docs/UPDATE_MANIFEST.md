# Directory Monitor — Publishing application updates

## Hosting contract

Host the manifest and full x64 installer on anonymous HTTPS endpoints. Redirects are allowed only when the final URL is also HTTPS. Publish the installer first, verify it is downloadable, and publish the manifest last so clients never discover an incomplete release.

The configured manifest URL is intentionally blank in new installations. Customers choose the endpoint in Settings.

## Manifest schema

```json
{
  "schemaVersion": 1,
  "version": "1.1.8",
  "publishedAtUtc": "2026-08-02T12:00:00Z",
  "releaseNotes": {
    "en": ["Added automatic update checking."],
    "vi": ["Bổ sung kiểm tra cập nhật tự động."]
  },
  "installer": {
    "url": "https://downloads.example.com/DirectoryMonitor-Setup-1.1.8-x64.exe",
    "sha256": "64_HEX_CHARACTERS",
    "sizeBytes": 190000000
  }
}
```

`version` must be a stable `major.minor.patch` value. The manifest is limited to 1 MB. The installer is limited to 1 GB and must exactly match both `sizeBytes` and SHA-256.

## Generate from the built installer

Build with Inno Setup 6 installed:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```

Then generate the manifest from the actual installer bytes:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/New-UpdateManifest.ps1 `
  -InstallerPath artifacts/installer/DirectoryMonitor-Setup-1.1.8-x64.exe `
  -InstallerUrl https://downloads.example.com/DirectoryMonitor-Setup-1.1.8-x64.exe `
  -ReleaseNotesEnPath docs/releases/1.1.8.en.txt `
  -ReleaseNotesViPath docs/releases/1.1.8.vi.txt
```

The script reads the release version from `Directory.Build.props`; an explicitly supplied `-Version` must match it. Upload the resulting `artifacts/update-manifest.json` only after uploading and testing the installer. Do not edit the generated version, hash, or size manually.

## Security and rollback

Directory Monitor enforces HTTPS, final redirect scheme, size, and SHA-256 before execution. HTTPS protects delivery and the manifest hash binds the installer bytes to that manifest. Protect the hosting account and TLS endpoint because the manifest is not separately signed.

These builds do not yet have an Authenticode certificate. Windows SmartScreen may warn users. Automatic downgrade and forced update are not supported. To roll back, publish a newer patch version containing the corrective build.
