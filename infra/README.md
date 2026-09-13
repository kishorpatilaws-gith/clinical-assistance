# Infrastructure

Terraform for the clinical AI platform. Introduced at Part 4, which is the first
part that creates durable cloud infrastructure — Parts 1-3 had nothing to declare
(Bedrock is a pure API call; Supabase was created through its dashboard).

## What Terraform owns

| Part | Resources |
|------|-----------|
| 4    | Cloudflare R2 bucket + scoped token; AWS IAM policy narrowing Bedrock access |
| 6    | S3 bucket, Bedrock Knowledge Base, embedding config, service roles |
| 8    | Bedrock Agent, action groups, agent execution role |
| 10   | Bedrock Guardrail |
| 12   | WAF, CloudFront, API Gateway, Cognito, observability |

## Setup

Terraform 1.13 or newer.

```powershell
cd infra
Copy-Item terraform.tfvars.example terraform.tfvars   # then fill in the account ID
$env:CLOUDFLARE_API_TOKEN = "<token with R2 admin scope>"
terraform init
terraform validate
terraform plan
```

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
