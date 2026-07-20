# Packaging

Estao uses MSIX packaging with App Installer for auto-updating distribution.

## Build & sign flow

### CI (every push/PR)

Build → test → publish self-contained → create unsigned `.msix` → upload as workflow artifact.

Unsigned MSIX can be sideloaded on dev machines with:
```powershell
Add-AppxPackage -AllowUnsigned -Path .\Estao-unsigned.msix
```

### Release (tag `v*`)

Same as CI plus:

1. **Patch version** — extract from tag `v1.2.3` → `1.2.3.0`
2. **Create MSIX** — `MakeAppx.exe pack`
3. **Sign with Azure Code Signing** — `AzureSignTool` using OIDC federation
4. **Generate .appinstaller** — from `AppInstaller.template.xml`
5. **GitHub Release** — attach `.msix` + `.appinstaller`

## GitHub secrets required for release

| Secret | Purpose |
|--------|---------|
| `AZURE_CLIENT_ID` | Azure service principal (federated/OIDC with GitHub) |
| `AZURE_TENANT_ID` | `4cd537bc-f481-4b44-bfca-72df4919fbc4` |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription |
| `AZURE_SIGNING_ENDPOINT` | Azure Code Signing endpoint URL |
| `AZURE_SIGNING_ACCOUNT` | Azure Code Signing account name |
| `AZURE_SIGNING_CERT_PROFILE` | Certificate profile name |
| `ESTAO_MSIX_PUBLISHER` | e.g. `CN=HMY` |
| `ESTAO_APPINSTALLER_URI` | HTTPS URL to the `.appinstaller` file on your hosting |
| `ESTAO_PACKAGE_URI` | HTTPS URL base where `.msix` files are hosted |

## Local build

```powershell
dotnet build
dotnet test
dotnet publish src\Hmy.Estao.App -c Release -r win-x64 --self-contained true
```

MSIX signing requires Azure Code Signing credentials; unsigned MSIX can be produced locally with MakeAppx.exe if you have the Windows SDK installed.