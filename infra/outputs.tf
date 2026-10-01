output "aws_region" {
  description = "Region the AWS provider is configured against."
  value       = var.aws_region
}

output "r2_bucket_name" {
  description = "Bucket holding synthetic clinical documents."
  value       = cloudflare_r2_bucket.clinical_documents.name
}

output "r2_bucket_location" {
  description = "Location hint R2 actually resolved for the bucket."
  value       = cloudflare_r2_bucket.clinical_documents.location
}

output "r2_bucket_created" {
  description = "When the bucket was created - predates Terraform, hence the import."
  value       = cloudflare_r2_bucket.clinical_documents.creation_date
}

output "r2_s3_endpoint" {
  description = "S3-compatible endpoint the .NET API points its S3 client at."
  value       = "https://${var.cloudflare_account_id}.r2.cloudflarestorage.com"
}
