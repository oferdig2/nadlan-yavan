-- 018: Asset table for agents, areas per user, Map / Table permissions (owner, 2026-10-25, remark #11).
-- Adds only. Every existing role gets USE_MAP, USE_ASSET_TABLE and ALL_AREAS, so nobody's access changes on deploy -
-- except the Agent role, which gets the new defaults: the table, no map, only the areas set on the user.
-- "Own" Assets are now the ones the user created (asset.created_by_user_id): the index below serves that filter.

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'USE_MAP', 'Map', 'Open the map page and use the map (parcel and Asset layers, overview, counts).', 'Role', NULL, 'Pages', 1
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'USE_MAP');

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'USE_ASSET_TABLE', 'Asset table', 'Open the Asset table page: enter and edit Assets in a table.', 'Role', NULL, 'Pages', 2
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'USE_ASSET_TABLE');

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'ALL_AREAS', 'Access all regions', 'Every geographic area. Without it the user sees only the areas set on their user - nothing outside them.', 'Role', NULL, 'Pages', 3
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'ALL_AREAS');

-- Every role there is today keeps what it had: map, table and all regions. Not the Agent role (below).
INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code IN ('USE_MAP', 'USE_ASSET_TABLE', 'ALL_AREAS')
WHERE r.code <> 'AGENT'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);

-- Agent / Mediator: the table (no map, no ALL_AREAS) and what the table needs. VIEW_ALL_PARCELS is narrowed by the
-- user's areas. Nothing is taken away (its file categories stay); no production user has this role (2026-10-25).
INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code IN ('USE_ASSET_TABLE', 'VIEW_ALL_PARCELS', 'CREATE_ASSET', 'VIEW_OWN_ASSET', 'EDIT_OWN_ASSET', 'UPLOAD_OWN_ASSET_FILE')
WHERE r.code = 'AGENT'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);

-- The geographic areas a user without ALL_AREAS may see. None = sees no Parcels at all.
CREATE TABLE app_user_area (
  user_id BIGINT NOT NULL,
  geographic_area_id INT NOT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (user_id, geographic_area_id),
  KEY ix_app_user_area_area (geographic_area_id),
  CONSTRAINT fk_app_user_area_user FOREIGN KEY (user_id) REFERENCES app_user (user_id) ON DELETE CASCADE,
  CONSTRAINT fk_app_user_area_area FOREIGN KEY (geographic_area_id) REFERENCES geographic_area (geographic_area_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE asset ADD KEY ix_asset_created_by (created_by_user_id);
