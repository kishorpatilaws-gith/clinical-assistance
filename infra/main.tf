terraform {
  required_version = ">= 1.13"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.25"
    }
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.64"
    }
  }

  # State is local for Part 4. It moves to a remote backend at Part 6, where R2
  # itself can host the state via the S3-compatible backend.
}

# The Cloudflare token is read from the CLOUDFLARE_API_TOKEN environment variable.
# It is deliberately NOT a Terraform variable: variables end up in terraform.tfvars,
# in the state file, and in plan output. Environment-only keeps it out of all three.
provider "cloudflare" {}

provider "aws" {
  region = var.aws_region

  # Credentials come from the default AWS credential chain — the same profile the
  # AWS CLI already uses. Nothing AWS-related is stored in this repo.

  default_tags {
    tags = local.common_tags
  }
}

locals {
  common_tags = {
    Project   = var.project
    Env       = var.environment
    ManagedBy = "terraform"
  }
}

# Resources live in their own files by concern: r2.tf for storage. Part 6 adds
# s3.tf, bedrock.tf and iam.tf when the Knowledge Base arrives.
