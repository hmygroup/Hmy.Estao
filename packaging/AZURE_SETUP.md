# Azure Code Signing + GitHub OIDC Setup

## Prerequisites

- Azure subscription with **Azure Code Signing** (Trusted Signing) enabled
- **Owner** or **Contributor** role in the Azure subscription
- **Admin** access to the GitHub repository

---

## Step 1: Create an Azure AD Application (service principal)

Open the [Azure Portal](https://portal.azure.com) → **Microsoft Entra ID** → **App registrations** → **New registration**

| Field | Value |
|-------|-------|
| Name | `estao-codesign` |
| Supported account types | **Accounts in this organizational directory only** |
| Redirect URI | Leave blank |

Click **Register**. On the overview page, copy these values:

| Value | Save as |
|-------|---------|
| Application (client) ID | → `AZURE_CLIENT_ID` |
| Directory (tenant) ID | → `AZURE_TENANT_ID` |
| Subscription ID | → `AZURE_SUBSCRIPTION_ID` |

---

## Step 2: Create Federated Credential for GitHub Actions

In the same app registration, go to **Certificates & secrets** → **Federated credentials** → **Add credential**

**Federated credential scenario**: Select **GitHub Actions deploying Azure resources**

| Field | Value |
|-------|-------|
| Organization | `hmygroup` |
| Repository | `Hmy.Estao` |
| Entity type | **Branch** — select `main` |
| Branch | `main` |

Click **Add**. This allows the release workflow running on `main` to authenticate to Azure.

> **Note**: The release workflow is triggered by tags, not branches. Federated credentials for **tags** are not directly supported in the portal UI. To allow tag-triggered releases, add a second federated credential with:
> - Entity type: **Environment** → `release`
> - Or use the **Issuer/Subject** format: `repo:hmygroup/Hmy.Estao:ref:refs/tags/v*`

---

## Step 3: Set up Azure Code Signing (Trusted Signing)

### 3.1 Enable the resource provider

In Azure Portal → **Subscriptions** → your subscription → **Resource providers** → search for `Microsoft.CodeSigning` → **Register**

### 3.2 Create a Code Signing account

Azure Portal → **Create a resource** → search for **Trusted Signing** → **Create**

| Field | Value |
|-------|-------|
| Subscription | Your subscription |
| Resource group | Create new, e.g. `rg-estao-codesign` |
| Account name | `estao-signing` |
| Region | Choose nearest (e.g. `West US`) |
| Pricing tier | Standard |

### 3.3 Create a Certificate Profile

After the account is created, go to the resource → **Certificate profiles** → **Create**

| Field | Value |
|-------|-------|
| Profile name | `estao-cert-profile` |
| Certificate type | **Private Trust** (internal app, no identity validation needed) |

Copy the **Signing endpoint URL** from the overview page → save as `AZURE_SIGNING_ENDPOINT`

---

## Step 4: Assign RBAC role to the service principal

Azure Portal → **Trusted Signing account** → **Access control (IAM)** → **Add role assignment**

| Field | Value |
|-------|-------|
| Role | **Trusted Signing Certificate Profile Signer** |
| Assign access to | **User, group, or service principal** |
| Select members | `estao-codesign` (the app you registered in Step 1) |

Click **Review + assign**.

---

## Step 5: Configure GitHub Secrets

Go to your GitHub repo → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**

Add these 9 secrets:

| Secret | Value |
|--------|-------|
| `AZURE_CLIENT_ID` | Client ID from Step 1 |
| `AZURE_TENANT_ID` | `4cd537bc-f481-4b44-bfca-72df4919fbc4` |
| `AZURE_SUBSCRIPTION_ID` | Your Azure subscription ID |
| `AZURE_SIGNING_ENDPOINT` | Signing endpoint URL from Step 3.3 |
| `AZURE_SIGNING_ACCOUNT` | `estao-signing` |
| `AZURE_SIGNING_CERT_PROFILE` | `estao-cert-profile` |
| `ESTAO_MSIX_PUBLISHER` | `CN=HMY` (must match your cert subject) |
| `ESTAO_APPINSTALLER_URI` | `https://hmygroup.github.io/Hmy.Estao/estao.appinstaller` |
| `ESTAO_PACKAGE_URI` | `https://hmygroup.github.io/Hmy.Estao/` |

---

## Step 6: Enable GitHub Pages

GitHub repo → **Settings** → **Pages** → set **Source** to **Deploy from a branch** → select `gh-pages` / `root`.

After the first release, the MSIX and AppInstaller will be available at:
- `https://hmygroup.github.io/Hmy.Estao/estao.appinstaller`
- `https://hmygroup.github.io/Hmy.Estao/Hmy.Estao_<version>_x64.msix`

---

## Step 7: Test the pipeline

### Trigger CI (unsigned MSIX)

Push any commit to `main`. The CI workflow will build, test, and upload an unsigned MSIX artifact.

### Create a release (signed MSIX)

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The release workflow will:
1. Build + test
2. Create MSIX
3. Sign with Azure Code Signing
4. Generate `.appinstaller`
5. Create a GitHub Release with both files

---

## Troubleshooting

### "Access denied" when signing

- Verify the service principal has the **Trusted Signing Certificate Profile Signer** role
- Check that the federated credential matches the ref format (`refs/tags/v*`)
- Ensure the Azure Code Signing resource provider is registered

### MakeAppx.exe not found in CI

The `windows-latest` runner includes the Windows SDK. If the workflow can't find it, add a step to install the SDK:

```yaml
- name: Install Windows SDK
  run: |
    choco install windows-sdk-10-version-2104-windbg -y
```

### MSIX version too high

MSIX version is `1.2.3.0`. The first three parts must be ≤ 65535. Our tags use `v0.x.y` so this is fine.

---

## Replace placeholder assets

The files in `src/Hmy.Estao.Packaging/Assets/` are 1×1 pixel placeholders. Replace them with real app icons:

| File | Size |
|------|------|
| `StoreLogo.png` | 50×50 |
| `Square44x44Logo.png` | 44×44 |
| `Square71x71Logo.png` | 71×71 |
| `Square150x150Logo.png` | 150×150 |
| `Wide310x150Logo.png` | 310×150 |
| `SplashScreen.png` | 620×300 |

You can use any tool (Paint.NET, GIMP, etc.) or an online favicon generator.