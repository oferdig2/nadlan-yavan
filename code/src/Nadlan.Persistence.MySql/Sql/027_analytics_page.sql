-- 027: the analytics page (owner, 2026-11-10) - Admin only; deleted public links keep their history (soft delete); an
-- optional customer (a Contact) on a public link, for the Admin's reports only. New columns and one permission; no data
-- changes.

ALTER TABLE presentation_link
  ADD COLUMN deleted_utc DATETIME(3) NULL COMMENT 'set = deleted: opens nothing, left out of the Present dialog; its visits still count on the analytics page',
  ADD COLUMN deleted_by_user_id BIGINT NULL,
  ADD COLUMN contact_id BIGINT NULL COMMENT 'the customer it was sent to - Admin only (VIEW_ANALYTICS_PAGE), never shown to the visitor or other staff',
  ADD KEY ix_presentation_link_contact (contact_id),
  ADD CONSTRAINT fk_presentation_link_deleted_by FOREIGN KEY (deleted_by_user_id) REFERENCES app_user (user_id),
  ADD CONSTRAINT fk_presentation_link_contact FOREIGN KEY (contact_id) REFERENCES contact (contact_id);

-- Admin has every permission; nobody else gets this one (owner, 2026-11-10: "the page is for Admin only").
INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'VIEW_ANALYTICS_PAGE', 'Analytics page', 'The analytics page of public links, and the customer a link was sent to.', 'Role', NULL, 'Pages', 7
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'VIEW_ANALYTICS_PAGE');
