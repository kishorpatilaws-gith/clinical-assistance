variable "cloudflare_account_id" {
  type        = string
  description = "Cloudflare account ID that owns the R2 bucket."

  validation {
    condition     = can(regex("^[0-9a-f]{32}$", var.cloudflare_account_id))
    error_message = "Cloudflare account ID must be 32 lowercase hex characters."
  }
}

variable "aws_region" {
  type        = string
  description = "AWS region for Bedrock and, from Part 6, S3 and Knowledge Bases."
  default     = "us-east-1"
}

variable "r2_bucket_name" {
  type        = string
  description = "R2 bucket holding synthetic clinical documents (Part 4)."
  default     = "clinical-documents"

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", var.r2_bucket_name))
    error_message = "Bucket name must be lowercase alphanumeric with hyphens, 3-63 characters."
  }
}

variable "r2_location" {
  type        = string
  description = "R2 location hint. APAC matches where the existing account data lives."
  default     = "APAC"
}

variable "project" {
  type        = string
  description = "Project tag applied to AWS resources."
  default     = "clinical-ai-platform"
}

variable "environment" {
  type        = string
  description = "Deployment environment tag."
  default     = "dev"

  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "Environment must be one of: dev, staging, prod."
  }
}
