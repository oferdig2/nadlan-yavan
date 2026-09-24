# Serving files through CloudFront (later)

The app already supports it; switching is configuration only. The bucket stays private.

1. **Distribution** — CloudFront → Create distribution → origin = the S3 bucket → *Origin access: Origin access control (OAC)* → let CloudFront update the bucket policy (or add the policy it shows).
2. **Signing key** — generate a key pair locally:
   ```
   openssl genrsa -out cf-private.pem 2048
   openssl rsa -pubout -in cf-private.pem -out cf-public.pem
   ```
   CloudFront → Public keys → add `cf-public.pem` → note its **ID**. Key groups → create one containing it.
3. **Restrict viewer access** — distribution → Behaviors → default → *Restrict viewer access: Yes* → trusted key group = the one above.
4. **Configure the app** (restart afterwards):
   ```
   .\config.ps1 set ms:host Nadlan:Storage:Delivery:Mode CloudFrontSigned
   .\config.ps1 set ms:host Nadlan:Storage:Delivery:CloudFrontDomain d1234abcd.cloudfront.net
   .\config.ps1 set ms:host Nadlan:Storage:Delivery:KeyPairId K2XXXXXXXXXXXX
   ```
   The private key is a secret: give it via environment variable rather than the DB, one line with `\n`:
   `$env:Nadlan__Storage__Delivery__PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nMIIE...\n-----END PRIVATE KEY-----"`
5. **Uploads are unchanged** — the browser still uploads straight to S3 with presigned part URLs; CloudFront is only for reading.

Production (EC2 / Fargate): don't use an access key. Give the instance/task an IAM role with the same policy as
`iam-policy-app.json`, leave `Nadlan:Storage:AwsProfile` empty, and add the production site origin to `s3-cors.json`.
