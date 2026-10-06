-- 010: Data person (user request 2026-10-06). Keeps the data: sees every Parcel, Asset, Portfolio, Contact and document,
-- and creates/edits all of it - polygons, KAEK, OT/plot, legal owners, Assets with any Managing Contact, prices,
-- Portfolios, Contacts, uploads in every file category.
-- Not included (still Admin only, or ticked per person in Admin > Roles): users/roles/access, lookup lists
-- (MANAGE_METADATA), deleting Parcels, moving a file type to another category.

INSERT INTO security_role (code, name, description, is_system, sort_order)
SELECT 'DATA_PERSON', 'Data person', 'Sees and edits all data (Parcels, Assets, Portfolios, Contacts, all documents); no administration.', 0, 25
WHERE NOT EXISTS (SELECT 1 FROM security_role WHERE code = 'DATA_PERSON');

INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code IN (
    'VIEW_ALL_PARCELS', 'EDIT_ALL_PARCELS', 'VIEW_LEGAL_OWNERS',
    'VIEW_ALL_ASSETS', 'EDIT_ALL_ASSETS', 'VIEW_PRICE',
    'VIEW_ALL_PORTFOLIOS', 'MANAGE_PORTFOLIOS',
    'VIEW_ALL_CONTACTS', 'MANAGE_CONTACTS',
    'VIEW_LEGAL_FILES', 'VIEW_ENGINEERING_FILES', 'VIEW_MARKETING_FILES', 'VIEW_CADASTRAL_FILES',
    'VIEW_PERMISSION_FILES', 'VIEW_GENERAL_FILES')
WHERE r.code = 'DATA_PERSON'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);
