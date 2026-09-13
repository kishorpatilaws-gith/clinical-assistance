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

# ------------------------------------------------------------------------------
# No resources yet. Step 4.0 exists to make step 4.1 reproducible: providers are
# pinned, variables are declared, and `terraform init` + `validate` pass against
# an empty plan. The R2 bucket and its scoped token arrive in 4.1.
# ------------------------------------------------------------------------------
