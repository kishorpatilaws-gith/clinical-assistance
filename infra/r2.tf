# The clinical-documents bucket already existed before Terraform did - it was
# created by hand in the dashboard while Part 4 was being built. So this is an
# `import`, not a create: Terraform adopts the live bucket rather than making a
# second one. Adopting existing infrastructure is the normal case in real work,
# and the proof it worked is a plan that reports no changes.
resource "cloudflare_r2_bucket" "clinical_documents" {
  account_id = var.cloudflare_account_id
  name       = var.r2_bucket_name
  location   = var.r2_location
}
