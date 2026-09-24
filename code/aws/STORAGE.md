# File storage (S3 + optional CloudFront)

Every storage setting lives in `app_config` (row `ms:host`, section `Nadlan:Storage`). Keys stored in the DB are
**relative to `RootFolder`**, so the bucket and folder can change without touching the database.

| Setting | Meaning |
|---|---|
| `Bucket` | S3 bucket name. Empty = file features off |
| `RootFolder` | Folder inside the bucket for all Nadlan files, e.g. `nadlan/dev`. Empty = bucket root |
| `Region` | Bucket region, e.g. `eu-central-1` |
| `AwsProfile` | Local AWS profile with the app's key (dev). Empty = default chain / IAM role (EC2, Fargate) |
| `PartSizeMb`, `MaxFileSizeGb`, `UploadUrlMinutes` | Upload tuning |
| `Delivery:Mode` | `S3Presigned` (dev) · `CloudFrontSigned` (prod, private bucket) · `CloudFrontPublic` |
| `Delivery:CloudFrontDomain`, `KeyPairId`, `CloudFrontOriginPath`, `UrlMinutes` | CloudFront delivery |
| `Delivery:PrivateKeyPem` | Secret: give it as env var `Nadlan__Storage__Delivery__PrivateKeyPem`, not in the DB |

Change a value: `.\config.ps1 set ms:host Nadlan:Storage:<Setting> <value>`, then restart the app.

## 1. Setup (shared or dedicated bucket)

```powershell
.\aws\setup-s3.ps1 -Bucket <bucket> -RootFolder nadlan/dev -Region eu-central-1 -AdminProfile <admin-profile>
```

On an existing bucket it changes nothing but its own CORS rule (`nadlan-app`) and lifecycle rule
(`nadlan-abort-incomplete-uploads`, scoped to the root folder); other rules and bucket settings are kept. The IAM user
it creates (`nadlan-<root>`, e.g. `nadlan-dev`) can reach only `s3://<bucket>/<RootFolder>/*`; its key goes into the local AWS
profile of the same name, and an existing key is reused only if AWS confirms it belongs to that user in that account.
The printed `config.ps1` commands point the app at it.

## 2. Moving to another bucket (e.g. the client's)

```powershell
# 1. set up the new place (their admin profile)
.\aws\setup-s3.ps1 -Bucket client-bucket -RootFolder nadlan/prod -Region eu-central-1 -AdminProfile client-admin
# 2. copy while running (big first copy; a profile that can read the old and write the new)
aws s3 sync s3://old-bucket/nadlan/dev s3://client-bucket/nadlan/prod --profile <profile>
# 3. STOP the app, copy again (only what arrived since step 2), switch, start
aws s3 sync s3://old-bucket/nadlan/dev s3://client-bucket/nadlan/prod --profile <profile>
.\config.ps1 set ms:host Nadlan:Storage:Bucket client-bucket
.\config.ps1 set ms:host Nadlan:Storage:RootFolder nadlan/prod
.\start-nadlan.ps1
```

No database changes: rows store `asset/5/<guid>/photo.jpg`; the app adds the root folder on every call. Keep the app
stopped between the second sync and the restart, otherwise uploads finishing in that window exist only in the old
bucket, and uploads still in progress can't complete (their multipart upload lives in the old bucket).
For a dedicated bucket with no folder use `-RootFolder ""` and `config.ps1 set ms:host Nadlan:Storage:RootFolder --empty`.

## 3. Serving through CloudFront

1. **Distribution**: origin = the bucket, *Origin access control (OAC)*, let CloudFront update the bucket policy.
   If you set an *Origin path* (e.g. `/nadlan/prod`), put the same value in `Delivery:CloudFrontOriginPath`.
2. **Signing key**:
   ```
   openssl genrsa -out cf-private.pem 2048
   openssl rsa -pubout -in cf-private.pem -out cf-public.pem
   ```
   CloudFront → Public keys → add `cf-public.pem` (note the **ID**) → Key groups → create one with it.
3. **Restrict viewer access**: Behaviors → default → *Restrict viewer access: Yes* → that key group.
4. **Configure** (restart afterwards):
   ```
   .\config.ps1 set ms:host Nadlan:Storage:Delivery:Mode CloudFrontSigned
   .\config.ps1 set ms:host Nadlan:Storage:Delivery:CloudFrontDomain d1234abcd.cloudfront.net
   .\config.ps1 set ms:host Nadlan:Storage:Delivery:KeyPairId K2XXXXXXXXXXXX
   $env:Nadlan__Storage__Delivery__PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nMIIE...\n-----END PRIVATE KEY-----"
   ```
Uploads don't change: the browser still uploads straight to S3; CloudFront only serves reads.

## 4. Production (EC2 / Fargate)

> **Not before the auth slice:** the API has no login yet, so anyone who can reach the app can list, open and delete
> files. Keep it on localhost / a private network until authentication and permissions are in.

No access keys: give the instance/task an IAM role with `iam-policy-app.json` (bucket + root folder filled in), keep
`AwsProfile` empty (the default), and run `setup-s3.ps1 -RootFolder nadlan/prod -AllowedOrigins https://your-domain`
so the production root gets its own CORS/lifecycle rules (origins are added, never removed).
