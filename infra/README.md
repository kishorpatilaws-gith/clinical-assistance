# Infrastructure

Terraform for the clinical AI platform. Manages the Cloudflare R2 bucket today;
Part 6 adds the AWS side (S3, Bedrock Knowledge Base, IAM service roles).

The `clinical-documents` bucket predates Terraform - it was created by hand while
Part 4 was being built - so it was brought under management with `terraform import`
rather than recreated. Adopting existing infrastructure is the normal case in real
work, and the proof it worked is `terraform plan` reporting no changes.

## What Terraform owns

| Part | Resources |
|------|-----------|
| 4    | Cloudflare R2 bucket `clinical-documents` (imported) |
| 6    | S3 bucket, Bedrock Knowledge Base, embedding config, IAM service roles |
| 8    | Bedrock Agent, action groups, agent execution role |
| 10   | Bedrock Guardrail |
| 12   | WAF, CloudFront, API Gateway, Cognito, observability |

## Setup

Terraform 1.13 or newer.

```powershell
cd infra
Copy-Item terraform.tfvars.example terraform.tfvars   # then fill in the account ID
$env:CLOUDFLARE_API_TOKEN = "cfut_..."                # keep the cfut_ prefix
terraform init
terraform plan          # expect: No changes
```

The token needs **Workers R2 Storage: Edit**, minted under My Profile -> API Tokens.
That is a different screen from R2 -> Manage R2 API Tokens, which issues the
S3-style access key / secret pair the .NET API uses. The two are not interchangeable:
S3 keys address objects inside a bucket, the API token manages the bucket itself.

Keep the `cfut_` prefix - it is part of the token, not a label.

AWS credentials come from the default credential chain — the same profile the AWS
CLI uses. Nothing AWS-related is stored in this repo.

## Secrets

Two rules, both enforced by `.gitignore`:

- `terraform.tfvars` is never committed. `terraform.tfvars.example` is the template.
- The Cloudflare token is passed by environment variable, not as a Terraform
  variable. Variables end up in the tfvars file, in state, and in plan output.

`.terraform.lock.hcl` **is** committed on purpose — it pins exact provider versions
and hashes, which is the point of a lock file.

## State

Local `terraform.tfstate` for Part 4. Moves to a remote backend at Part 6, where R2
can host its own state over the S3-compatible API.

> State contains resource attributes in plaintext, including some secrets. It is
> gitignored, and stays that way.
