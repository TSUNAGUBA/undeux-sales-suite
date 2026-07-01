# デプロイ手順書（GitHub Actions 自動デプロイ）

フロントエンドを **Firebase Hosting**、バックエンドAPIを **AWS EC2 (Ubuntu)** へ
GitHub Actions で自動デプロイするための、初回セットアップ手順書です。

- すべて **Windows PowerShell** のコマンドで実行できます。上から順にコピー＆実行してください。
- 一度セットアップすれば、2回目以降のデプロイはボタン1つ（コマンド1行）です。
- コマンドの出力で得た値は、次のステップで使うため **同じ PowerShell ウィンドウ** を開いたまま進めてください。

## 全体像

```mermaid
graph LR
    Dev[開発者] -->|gh workflow run| ALL[deploy-all 一括デプロイ]
    ALL -->|1. 呼び出し| BE[deploy-backend]
    ALL -->|2. backend 成功後| FE[deploy-frontend]
    Dev -. 個別実行も可 .-> BE
    Dev -. 個別実行も可 .-> FE
    FE --> FH[Firebase Hosting]
    BE -->|SSH| EC2[AWS EC2 Ubuntu]
    EC2 --> RDS[(AWS RDS PostgreSQL)]
    ALL -. 設定値 .-> SEC[Repository Secrets]
```

## 事前に用意するもの

| # | 必要なもの | 備考 |
|---|-----------|------|
| 1 | AWS アカウント | RDS・EC2 の作成（無料利用枠の対象外の課金が発生します） |
| 2 | Google アカウント | Firebase プロジェクト用 |
| 3 | 独自ドメイン名 | API を HTTPS 公開するため。例 `api.example.com` のサブドメインを1つ使用 |
| 4 | GitHubリポジトリへの管理者権限 | Secrets 登録・ワークフロー実行のため |

---

## ステップ0: 必要なツールのインストール

PowerShell を **「管理者として実行」** で開き、以下を実行します。

```powershell
winget install --id GitHub.cli -e
winget install --id Amazon.AWSCLI -e
winget install --id OpenJS.NodeJS.LTS -e
```

インストール後、**PowerShell をいったん閉じて開き直します**（PATH を反映するため）。
以降は管理者でない通常の PowerShell で構いません。Firebase CLI を入れます。

```powershell
npm install -g firebase-tools
```

各サービスにログインします（ブラウザが開きます）。

```powershell
gh auth login        # GitHub: ブラウザ認証を選択
firebase login       # Google アカウントでログイン
```

AWS の認証情報を設定します。AWS マネジメントコンソールの
**IAM → ユーザー → 対象ユーザー → セキュリティ認証情報 → アクセスキーを作成**
で取得したキーを入力してください。

> このアクセスキーは本手順（RDS・EC2 の作成）でのみ使用します。GitHub Actions の
> デプロイワークフローは AWS 認証情報を使用しません。**セットアップ完了後、この
> アクセスキーは無効化・削除してください。** 権限は EC2・RDS 操作に絞るのが望ましく
> （最小権限の原則）、簡便にはセットアップ時のみ広い権限を付与し、完了後にキーを削除します。

```powershell
aws configure
# AWS Access Key ID     : （入力）
# AWS Secret Access Key : （入力）
# Default region name   : ap-northeast-1
# Default output format : json
```

---

## ステップ1: Firebase のセットアップ

### 1-1. プロジェクトの作成

プロジェクトIDは全世界で一意です（例 `undeux-sales-12345`）。

```powershell
$FirebaseProjectId = "undeux-sales-12345"   # 任意の一意なIDに変更
firebase projects:create $FirebaseProjectId --display-name "UndeuxSales"
```

### 1-2. Webアプリの登録と設定値の取得

```powershell
firebase apps:create WEB "undeux-web" --project $FirebaseProjectId
firebase apps:sdkconfig WEB --project $FirebaseProjectId
```

出力された設定のうち、次の2つを控えて変数に設定します。

```powershell
$FirebaseApiKey    = "（出力の apiKey の値）"
$FirebaseAuthDomain = "$FirebaseProjectId.firebaseapp.com"
```

### 1-3. Authentication（メール/パスワード）の有効化

ブラウザで [Firebase コンソール](https://console.firebase.google.com/) を開き、
作成したプロジェクトで以下を操作します（この手順のみ画面操作）。

1. 左メニュー **構築 → Authentication** → **始める**
2. **Sign-in method** タブ → **メール/パスワード** → **有効にする** → 保存
3. **Users** タブ → **ユーザーを追加** で、アプリにログインする利用者を登録

### 1-4. デプロイ用サービスアカウント鍵の取得

1. Firebase コンソール左上の **歯車 → プロジェクトの設定**
2. **サービス アカウント** タブ → **新しい秘密鍵の生成** → JSONファイルがダウンロードされる
3. このファイルのパスを控えます（例 `C:\Users\you\Downloads\undeux-sales-xxxx.json`）

```powershell
$FirebaseServiceAccountPath = "C:\Users\you\Downloads\（ダウンロードしたファイル名）.json"
```

> **権限（重要）:** Firebase Hosting へデプロイするサービスアカウントには **Firebase Hosting 管理者**
> （`roles/firebasehosting.admin`）ロールが必要です（Firebase の「サービスアカウント」タブで生成した
> 既定のアカウントは付与済み）。加えて、対象プロジェクトで **Firebase Hosting API** が有効である必要があります。
> `deploy-frontend` が `Failed to authenticate` で失敗する場合は、まず `FIREBASE_SERVICE_ACCOUNT` シークレットに
> **この JSON の全文**が登録されているか（空・一部欠けでないか）を確認してください（ワークフローの
> 「サービスアカウントの事前検証」ステップのログに `project_id` / `client_email` が出ます）。

---

## ステップ2: AWS RDS（PostgreSQL）の作成

```powershell
# デフォルトVPCのIDを取得
$VpcId = aws ec2 describe-vpcs --filters "Name=isDefault,Values=true" --query "Vpcs[0].VpcId" --output text
$VpcId   # 確認（vpc-xxxx と表示される）

# RDS 用セキュリティグループを作成
$RdsSgId = aws ec2 create-security-group --group-name undeux-rds-sg --description "UndeuxSales RDS" --vpc-id $VpcId --query "GroupId" --output text

# DBパスワードを自動生成する（英数字24文字。記号を含まず接続文字列で安全）
$DbPassword = -join ((65..90) + (97..122) + (48..57) | Get-Random -Count 24 | ForEach-Object { [char]$_ })

# RDS インスタンスを作成
aws rds create-db-instance `
  --db-instance-identifier undeux-db `
  --db-instance-class db.t3.micro `
  --engine postgres `
  --engine-version 16 `
  --master-username undeux `
  --master-user-password $DbPassword `
  --allocated-storage 20 `
  --storage-type gp3 `
  --db-name undeux `
  --vpc-security-group-ids $RdsSgId `
  --no-publicly-accessible `
  --backup-retention-period 7 `
  --no-multi-az

# 作成完了まで待つ（5〜10分。完了すると次の行に進みます）
aws rds wait db-instance-available --db-instance-identifier undeux-db

# 接続先（エンドポイント）を取得
$RdsEndpoint = aws rds describe-db-instances --db-instance-identifier undeux-db --query "DBInstances[0].Endpoint.Address" --output text
$RdsEndpoint   # 確認

# API が使う接続文字列を組み立てる
$RdsConnectionString = "Host=$RdsEndpoint;Port=5432;Database=undeux;Username=undeux;Password=$DbPassword;Command Timeout=600"
```

> `--engine-version 16` でエラーが出る場合は、`aws rds describe-db-engine-versions --engine postgres --query "DBEngineVersions[].EngineVersion"` で利用可能なバージョンを確認し、その値（例 `16.6`）に変更してください。

---

## ステップ3: AWS EC2（Ubuntu）の作成

> **API の公開方式:** 本プロジェクトの API は、EC2 上で稼働する `nginx-proxy` +
> `acme-companion` のバックエンドとして公開されます（`api` コンテナに `VIRTUAL_HOST`
> を付与し、共有 Docker ネットワーク `tsunaguba-dev-001` に接続）。複数 API を相乗り
> させる共用 EC2 を前提とした構成です。専用 EC2 を新規に用意する場合は、先に
> `nginx-proxy` / `acme-companion` をセットアップしておく必要があります。

### 3-1. SSH鍵ペアの生成

デプロイ用のSSH鍵を作成します。**パスフレーズは空のまま Enter を2回**押してください。

```powershell
ssh-keygen -t ed25519 -f "$HOME\.ssh\undeux-ec2" -C "undeux-ec2-deploy"

# 公開鍵を AWS に登録
aws ec2 import-key-pair --key-name undeux-ec2 --public-key-material "fileb://$HOME/.ssh/undeux-ec2.pub"
```

> 既に EC2 用のキーペアを作成済みで、秘密鍵が PuTTY 形式（`.ppk`）しか手元にない場合は、
> このステップの代わりに **付録A** で `.ppk` を OpenSSH 形式へ変換した鍵を使います。

### 3-2. EC2 用セキュリティグループの作成

```powershell
$Ec2SgId = aws ec2 create-security-group --group-name undeux-ec2-sg --description "UndeuxSales EC2" --vpc-id $VpcId --query "GroupId" --output text

# SSH(22)・HTTP(80)・HTTPS(443) を許可
# 22 は SSH 鍵認証のみ（パスワード認証は無効）。GitHub Actions から接続するため全許可とする。
aws ec2 authorize-security-group-ingress --group-id $Ec2SgId --protocol tcp --port 22  --cidr 0.0.0.0/0
aws ec2 authorize-security-group-ingress --group-id $Ec2SgId --protocol tcp --port 80  --cidr 0.0.0.0/0
aws ec2 authorize-security-group-ingress --group-id $Ec2SgId --protocol tcp --port 443 --cidr 0.0.0.0/0
```

> SSH(22) は鍵認証のみ（パスワード認証は無効）で全世界公開とします（GitHub Actions の
> ランナーIPが変動するため）。本番でアクセス元を限定したい場合は、22番を管理元IPに絞るか、
> AWS Systems Manager Session Manager 経由の運用を検討してください。

### 3-3. EC2 インスタンスの起動

```powershell
# 最新の Ubuntu 24.04 AMI ID を取得
$AmiId = aws ssm get-parameters --names /aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id --query "Parameters[0].Value" --output text

# ルートディスクを 30GB に指定するファイルを作成
'[{"DeviceName":"/dev/sda1","Ebs":{"VolumeSize":30,"VolumeType":"gp3"}}]' | Set-Content -Path bdm.json -NoNewline

# インスタンスを起動
$InstanceId = aws ec2 run-instances `
  --image-id $AmiId `
  --instance-type t3.medium `
  --key-name undeux-ec2 `
  --security-group-ids $Ec2SgId `
  --block-device-mappings file://bdm.json `
  --query "Instances[0].InstanceId" --output text

aws ec2 wait instance-running --instance-ids $InstanceId
```

### 3-4. 固定IP（Elastic IP）の割り当て

```powershell
$AllocId = aws ec2 allocate-address --query "AllocationId" --output text
aws ec2 associate-address --instance-id $InstanceId --allocation-id $AllocId
$Ec2Ip = aws ec2 describe-addresses --allocation-ids $AllocId --query "Addresses[0].PublicIp" --output text
$Ec2Ip   # 確認（このIPをDNSに設定します）
```

### 3-5. EC2 への Docker 導入

```powershell
ssh -o StrictHostKeyChecking=accept-new -i "$HOME\.ssh\undeux-ec2" ubuntu@$Ec2Ip "curl -fsSL https://get.docker.com -o /tmp/get-docker.sh && sudo sh /tmp/get-docker.sh && sudo usermod -aG docker ubuntu"
```

---

## ステップ4: RDS への接続を EC2 から許可

```powershell
aws ec2 authorize-security-group-ingress --group-id $RdsSgId --protocol tcp --port 5432 --source-group $Ec2SgId
```

---

## ステップ5: DNS の設定

API を HTTPS 公開するため、用意したドメインのサブドメインを EC2 の固定IPに向けます。

1. ドメインを管理しているサービス（お名前.com、Route 53 等）の DNS 設定を開く
2. 次の **A レコード** を追加する

   | ホスト名 | 種別 | 値 |
   |---------|------|-----|
   | `api`（例: `api.example.com`） | A | `$Ec2Ip` で表示されたIP |

3. 使用するドメイン名を変数に設定

```powershell
$ApiDomain = "api.example.com"   # 実際に設定したサブドメインに変更
```

> DNS の反映には数分〜数十分かかることがあります。反映後、EC2 上の nginx-proxy（acme-companion）が自動でHTTPS証明書を取得します。

---

## ステップ6: GitHub リポジトリシークレットの登録

これまでに得た値を、GitHub のリポジトリシークレットに登録します。

```powershell
$Repo = "tsunaguba/undeux-sales-suite"

gh secret set FIREBASE_PROJECT_ID  --repo $Repo --body $FirebaseProjectId
gh secret set FIREBASE_API_KEY     --repo $Repo --body $FirebaseApiKey
gh secret set FIREBASE_AUTH_DOMAIN --repo $Repo --body $FirebaseAuthDomain
gh secret set API_DOMAIN           --repo $Repo --body $ApiDomain
gh secret set FRONTEND_ORIGIN      --repo $Repo --body "https://$FirebaseProjectId.web.app"
gh secret set RDS_CONNECTION_STRING --repo $Repo --body $RdsConnectionString
gh secret set EC2_HOST             --repo $Repo --body $Ec2Ip
gh secret set EC2_USER             --repo $Repo --body "ubuntu"

# ファイルから登録するもの（サービスアカウントJSON・SSH秘密鍵）
Get-Content $FirebaseServiceAccountPath -Raw | gh secret set FIREBASE_SERVICE_ACCOUNT --repo $Repo
Get-Content "$HOME\.ssh\undeux-ec2" -Raw    | gh secret set EC2_SSH_KEY --repo $Repo

# 登録結果を確認（10件表示されればOK）
gh secret list --repo $Repo

# 機密値を含む PowerShell 変数を消去する（コンソール履歴対策）
Remove-Variable DbPassword, RdsConnectionString -ErrorAction SilentlyContinue
```

---

## ステップ7: デプロイの実行

> ⚠️ ワークフローは **デフォルトブランチ（main）** に存在する必要があります。
> 先に Pull Request をマージしてから実行してください。

```powershell
# 一括デプロイ（バックエンド → フロントエンドの順で自動実行）
gh workflow run deploy-all.yml --repo $Repo --ref main

# 進行状況の確認
gh run list --repo $Repo --limit 5
gh run watch --repo $Repo
```

> 個別にデプロイしたい場合は `deploy-backend.yml` / `deploy-frontend.yml` を
> 単独で実行できます（従来どおり）。deploy-all はバックエンド失敗時に
> フロントエンドのデプロイを自動的に中止します。

- **deploy-backend** は初回約8〜12分（**イメージビルドは CI（GitHub Actions）で実行**し GHCR へ push、
  EC2 は pull + 初期データ約160万行の投入）。2回目以降は初期投入がスキップされ数分。
- **deploy-frontend** は約2〜3分。
- GitHub の **Actions** タブからも進行状況・ログを確認できます。

> **バックエンドのビルド方式（2026-07 改修）:** EC2 のルートディスク枯渇（`.NET SDK` 展開で
> `no space left on device`）を恒久回避するため、API/DataLoader のイメージは **CI（Actions ランナー）で
> ビルドして GHCR（`ghcr.io/<owner>/<repo>-api` / `-dataloader`）へ push** し、EC2 は `docker compose pull`
> するだけに変更しました。前提は2つだけです:
> 1. リポジトリの **Actions に packages 書込権限**（ワークフローの `permissions: packages: write` で付与済み。
>    Organization 側で GITHUB_TOKEN のパッケージ作成を制限している場合は許可が必要）。
> 2. **EC2 から `ghcr.io` への outbound 通信**（従来 `mcr.microsoft.com` へ到達できているため通常は問題なし）。
> GHCR ログインはワークフローが一時トークン（`GITHUB_TOKEN`）を `--password-stdin` で渡し、
> デプロイ後に EC2 側で `docker logout` します（永続シークレット不要）。

---

## ステップ8: 動作確認

```powershell
# API のヘルスチェック（status: ready が返ればOK）
Invoke-RestMethod "https://$ApiDomain/api/health/ready"

# フロントエンドを開く
Start-Process "https://$FirebaseProjectId.web.app"
```

ブラウザでログイン画面が表示され、ステップ1-3で登録した利用者でログインできれば成功です。

> **取込機能を使う利用者** には、取込権限ロール（`role=admin`）の付与が必要です。
> 詳細は `infra/README.md` の「認証」を参照してください。

---

## 2回目以降のデプロイ

コードを更新したら、main にマージ後、次のコマンドを実行するだけです。

```powershell
gh workflow run deploy-all.yml --repo tsunaguba/undeux-sales-suite --ref main
```

個別にデプロイする場合（片方だけ更新したとき等）:

```powershell
gh workflow run deploy-backend.yml  --repo tsunaguba/undeux-sales-suite --ref main
gh workflow run deploy-frontend.yml --repo tsunaguba/undeux-sales-suite --ref main
```

GitHub の **Actions** タブの「Run workflow」ボタンからも実行できます。
初期データは投入済みのためスキップされ、2回目以降のバックエンドデプロイは数分で完了します。

---

## 設定値早見表（リポジトリシークレット）

| シークレット名 | 内容 | 取得元 |
|---------------|------|--------|
| `FIREBASE_PROJECT_ID` | Firebase プロジェクトID | ステップ1-1 |
| `FIREBASE_API_KEY` | Web アプリの apiKey | ステップ1-2 |
| `FIREBASE_AUTH_DOMAIN` | `<projectId>.firebaseapp.com` | ステップ1-2 |
| `FIREBASE_SERVICE_ACCOUNT` | サービスアカウントJSON（全文） | ステップ1-4 |
| `API_DOMAIN` | API のドメイン名（例 `api.example.com`） | ステップ5 |
| `FRONTEND_ORIGIN` | フロントエンドのURL（例 `https://<projectId>.web.app`） | ステップ1-1 |
| `RDS_CONNECTION_STRING` | RDS への接続文字列 | ステップ2 |
| `EC2_HOST` | EC2 の固定IP | ステップ3-4 |
| `EC2_USER` | `ubuntu` | 固定 |
| `EC2_SSH_KEY` | SSH秘密鍵（`undeux-ec2` ファイル全文） | ステップ3-1 |

---

## トラブルシューティング

| 症状 | 対処 |
|------|------|
| `deploy-backend` が**イメージビルド（CI）**で失敗 | Actions のログで `dotnet publish` のコンパイルエラーを確認して修正する（ビルドは Actions ランナー上で実行される）。GHCR への push で `denied` なら、リポジトリ/Organization の Actions パッケージ書込権限（`permissions: packages: write` の許可）を確認する |
| EC2 の `docker compose pull` が失敗 | EC2 が `ghcr.io` へ到達できるか（outbound 443）と、ワークフローの GHCR ログインが成功しているかを確認する。手動時は EC2 で `docker login ghcr.io` 済みか確認 |
| `no space left on device`（旧構成の名残） | 本改修後、EC2 では **ビルドしない**ためこの失敗は原則発生しない。残存する旧イメージ/キャッシュで逼迫する場合は EC2 に SSH して `docker builder prune -af && docker image prune -af`（`--volumes` は付けない）。恒常的に不足するなら EBS 拡張（ステップ3-3 は 30GB 指定）を確認する |
| API に HTTPS で繋がらない | DNS の A レコードが EC2 の固定IPを指しているか、反映済みか確認。nginx-proxy（acme-companion）の証明書取得には 80/443 の開放とDNS反映が必要 |
| フロントから API 呼び出しが CORS エラー | `FRONTEND_ORIGIN` シークレットが実際のフロントURLと一致しているか確認し、`deploy-backend` を再実行 |
| `deploy-frontend` が `Failed to authenticate` で失敗 | サービスアカウント認証の問題。ワークフローの「サービスアカウントの事前検証」ステップのログを確認する: ①`FIREBASE_SERVICE_ACCOUNT` が空/不正JSON → 鍵JSONの全文を登録し直す（ステップ1-4） ②`project_id` 不一致の警告 → 鍵と `FIREBASE_PROJECT_ID` のプロジェクトを揃える ③検証は通るが失敗 → サービスアカウントに **Firebase Hosting 管理者** ロール、対象プロジェクトで **Firebase Hosting API** 有効化を確認 |
| ログインできない | Firebase の Authentication でメール/パスワードが有効か、利用者が登録済みか確認 |
| 取込が 403 になる | 取込する利用者に `role=admin` カスタムクレームが必要（`infra/README.md` 参照） |
| EC2 上のログを見たい | `ssh -i "$HOME\.ssh\undeux-ec2" ubuntu@<EC2IP>` で接続し `cd undeux-sales-suite/infra/aws; docker compose -f docker-compose.ec2.yml --env-file .env logs api` |
| `deploy-backend` の SSH 認証が失敗する | `EC2_SSH_KEY` シークレットを `Get-Content -Raw` で登録し直す（改行コード混入時の対処） |
| `puttygen` で `unrecognised option '-O'` エラー | Windows の `puttygen.exe`（GUI 版）はコマンドライン変換に非対応。**付録A** の GUI 手順で `.ppk` を変換する |

EC2 上での手動運用コマンドは `infra/aws/README.md` を参照してください。

---

## 付録A: 既存の `.ppk` 鍵を OpenSSH 形式へ変換する

EC2 用の秘密鍵が **PuTTY 形式（`.ppk`）** しか手元にない場合（AWS でキーペアを作成した際に
`.ppk` 形式でダウンロードした等）は、ステップ3-1 で鍵を新規生成する代わりに、その `.ppk` を
**OpenSSH 形式へ変換**して使います。`ssh` コマンドや GitHub Actions のランナーは OpenSSH
形式の鍵を前提とするため、`.ppk` のままでは利用できません。

> **Windows では GUI で変換します。** Windows 版 PuTTY の `puttygen.exe` は GUI 版で、
> コマンドラインでの鍵変換（`-O` オプション等）に対応していません（`puttygen ... -O ...` を
> 実行すると `unrecognised option '-O'` エラーになります）。`-O` でのコマンドライン変換は
> Unix 版 `puttygen`（`putty-tools`）の機能です。

事前に、保存先の `.ssh` フォルダを作成しておきます。

```powershell
New-Item "$HOME\.ssh" -ItemType Directory -Force | Out-Null
```

続いて PuTTYgen の GUI で変換します。

1. **PuTTYgen を起動** — スタートメニューで「PuTTYgen」を検索して開く。
2. **「Load」** ボタンをクリックし、変換元の `.ppk` ファイルを選択して開く。
   `.ppk` にパスフレーズが設定されている場合は入力を求められるので入力する。
3. **「Key passphrase」「Confirm passphrase」欄は空のまま**にする（空欄 = パスフレーズ
   なしで書き出す。GitHub Actions のデプロイ鍵には必須）。
4. メニューバーの **「Conversions」→「Export OpenSSH key」** をクリックする。
5. パスフレーズなしで保存してよいか確認が出たら **「はい」** を選ぶ。
6. 保存ダイアログで `.ssh` フォルダ（`C:\Users\<ユーザー名>\.ssh\`）へ移動し、「保存の種類」
   は All Files のまま、ファイル名 **`undeux-ec2`**（拡張子なし）で保存する。

変換後、先頭行を確認します。

```powershell
Get-Content "$HOME\.ssh\undeux-ec2" -TotalCount 1
```

`-----BEGIN OPENSSH PRIVATE KEY-----`（鍵の種類によっては `-----BEGIN RSA PRIVATE KEY-----`）
と表示されれば成功です。いずれも OpenSSH 形式の秘密鍵で、以降は `$HOME\.ssh\undeux-ec2` を
ステップ3-1 で生成する鍵と同じものとして扱えます（ステップ6 の `EC2_SSH_KEY` 登録もこの
ファイルを使います）。

補足:

- コマンドラインで変換したい場合は、Unix 版 `puttygen` を使います（`-O` 対応）。WSL で
  `sudo apt install -y putty-tools` を実行後、`puttygen <.ppk のパス> -O private-openssh-new -o <出力先>`。
- この `.ppk` に対応する AWS キーペアで EC2 を作成済みの場合は、ステップ3-1 の鍵生成・
  `import-key-pair` とステップ3-3 の `--key-name` を、その既存キーペア名に読み替えます。
