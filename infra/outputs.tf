# Outputs land here in 4.1, once the R2 bucket exists: bucket name, S3-compatible
# endpoint, and the scoped token ID. The token *value* is written as a sensitive
# output and must be moved into `dotnet user-secrets` — never committed.

output "aws_region" {
  description = "Region the AWS provider is configured against."
  value       = var.aws_region
}
