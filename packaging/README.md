# MSIX / App Installer Scaffolding

MVP packaging uses MSIX plus an App Installer feed. The repository intentionally contains placeholders only.

Expected CI inputs:

- `ESTAO_MSIX_PUBLISHER`: certificate publisher subject, for example `CN=HMY`.
- `ESTAO_MSIX_CERTIFICATE_PATH`: path to the signing certificate in CI.
- `ESTAO_MSIX_CERTIFICATE_PASSWORD`: certificate password supplied by CI secret storage.
- `ESTAO_APPINSTALLER_URI`: HTTPS URL for the generated `.appinstaller` file.
- `ESTAO_PACKAGE_URI`: HTTPS base URL where MSIX packages are hosted.

The local build can still publish portable self-contained binaries with `dotnet publish`; MSIX signing requires real release inputs.
