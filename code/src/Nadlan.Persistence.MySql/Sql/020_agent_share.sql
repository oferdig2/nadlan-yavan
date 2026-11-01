-- 020: "Let agent know" (owner, 2026-10-31, N2) and the texts of the "own Asset" permissions (C6).
-- An Admin or Data person ticking "Let agent know" on an Asset gives the agent linked to its Managing Contact view and
-- edit (EDIT_ASSET) - marked here, so unticking it or changing the contact removes exactly that grant and never one
-- given by hand in Admin > Users > Access to objects. Existing grants stay as they are (source NULL = by hand).

ALTER TABLE resource_access
  ADD COLUMN source VARCHAR(20) NULL COMMENT 'AGENT_SHARE = given by "Let agent know" on the Asset (removed with it); NULL = given by hand';

-- Since 018 "own" means created by the user, not managed by the user's Contact: only the hover texts change.
UPDATE permission SET description = 'Create Assets; the user''s own Contact becomes their Managing Contact.' WHERE code = 'CREATE_ASSET';
UPDATE permission SET description = 'Assets the user created.' WHERE code = 'VIEW_OWN_ASSET';
UPDATE permission SET description = 'Edit Assets the user created.' WHERE code = 'EDIT_OWN_ASSET';
UPDATE permission SET description = 'Upload files to Assets the user created.' WHERE code = 'UPLOAD_OWN_ASSET_FILE';
