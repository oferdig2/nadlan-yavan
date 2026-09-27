-- 007: Users, security roles, permissions, explicit resource access, password tokens (spec §4 User/Role/Permission/
-- RolePermission/ResourceAccess, Appendix 1 §1-2, §9.3, §10-11).
--
-- There is no self-registration: an Admin creates every user. A user signs in with Google (matched by email) and/or a
-- password the Admin set (or the user set through a reset link). Revoking a login = deactivating the user.

-- Security roles (who the user IS for permissions). Not the same as contact_role (what a Contact is in business).
CREATE TABLE security_role (
  security_role_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL,
  description VARCHAR(500) NULL,
  is_system TINYINT(1) NOT NULL DEFAULT 0 COMMENT '1 = ADMIN: sees and does everything, permissions not editable',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (security_role_id),
  UNIQUE KEY uq_security_role_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The permission catalog. scope Role = given to a role (applies globally or to own objects);
-- scope Resource = granted on one object through resource_access (resource_type says which kind).
CREATE TABLE permission (
  permission_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(60) NOT NULL,
  name VARCHAR(150) NOT NULL,
  description VARCHAR(500) NULL,
  scope VARCHAR(10) NOT NULL,
  resource_type VARCHAR(20) NULL,
  group_name VARCHAR(40) NOT NULL,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (permission_id),
  UNIQUE KEY uq_permission_code (code),
  CONSTRAINT ck_permission_scope CHECK (scope IN ('Role', 'Resource')),
  CONSTRAINT ck_permission_resource CHECK ((scope = 'Role' AND resource_type IS NULL)
    OR (scope = 'Resource' AND resource_type IN ('Parcel', 'Asset', 'Portfolio', 'Contact')))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE role_permission (
  security_role_id INT NOT NULL,
  permission_id INT NOT NULL,
  PRIMARY KEY (security_role_id, permission_id),
  KEY ix_role_permission_permission (permission_id),
  CONSTRAINT fk_role_permission_role FOREIGN KEY (security_role_id) REFERENCES security_role (security_role_id) ON DELETE CASCADE,
  CONSTRAINT fk_role_permission_permission FOREIGN KEY (permission_id) REFERENCES permission (permission_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A login. email is stored lower-case; utf8mb4_bin so the unique key is exact.
-- session_version is copied into the sign-in cookie; bumping it signs the user out everywhere.
CREATE TABLE app_user (
  user_id BIGINT NOT NULL AUTO_INCREMENT,
  email VARCHAR(320) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  display_name VARCHAR(200) NOT NULL,
  contact_id BIGINT NULL COMMENT 'business identity: owner-derived access (Asset.managing_contact_id = this)',
  security_role_id INT NOT NULL,
  password_hash VARCHAR(255) NULL COMMENT 'NULL = no password; Google sign-in only',
  password_changed_utc DATETIME(3) NULL,
  must_change_password TINYINT(1) NOT NULL DEFAULT 0,
  failed_login_count INT NOT NULL DEFAULT 0,
  locked_until_utc DATETIME(3) NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1 COMMENT '0 = login revoked',
  session_version INT NOT NULL DEFAULT 1,
  last_login_utc DATETIME(3) NULL,
  last_login_method VARCHAR(20) NULL COMMENT 'Password | Google',
  created_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (user_id),
  UNIQUE KEY uq_app_user_email (email),
  UNIQUE KEY uq_app_user_contact (contact_id),
  KEY ix_app_user_role (security_role_id),
  CONSTRAINT fk_app_user_contact FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
  CONSTRAINT fk_app_user_role FOREIGN KEY (security_role_id) REFERENCES security_role (security_role_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Explicit access to one object (spec §8.3): "Lawyer X may VIEW_ASSET on Asset 123".
-- Portfolio access also grants its Assets (user decision 2026-09-24).
CREATE TABLE resource_access (
  resource_access_id BIGINT NOT NULL AUTO_INCREMENT,
  user_id BIGINT NOT NULL,
  resource_type VARCHAR(20) NOT NULL,
  resource_id BIGINT NOT NULL,
  permission_id INT NOT NULL,
  granted_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  expires_utc DATETIME(3) NULL,
  PRIMARY KEY (resource_access_id),
  UNIQUE KEY uq_resource_access (user_id, resource_type, resource_id, permission_id),
  KEY ix_resource_access_resource (resource_type, resource_id),
  CONSTRAINT fk_resource_access_user FOREIGN KEY (user_id) REFERENCES app_user (user_id) ON DELETE CASCADE,
  CONSTRAINT fk_resource_access_permission FOREIGN KEY (permission_id) REFERENCES permission (permission_id),
  CONSTRAINT ck_resource_access_type CHECK (resource_type IN ('Parcel', 'Asset', 'Portfolio', 'Contact'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One-time links to set a password: Reset (forgot password, by email) or Invite (created by an Admin).
-- Only the SHA-256 of the token is stored.
CREATE TABLE password_token (
  token_hash CHAR(64) NOT NULL,
  user_id BIGINT NOT NULL,
  purpose VARCHAR(20) NOT NULL,
  created_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  expires_utc DATETIME(3) NOT NULL,
  used_utc DATETIME(3) NULL,
  PRIMARY KEY (token_hash),
  KEY ix_password_token_user (user_id),
  CONSTRAINT fk_password_token_user FOREIGN KEY (user_id) REFERENCES app_user (user_id) ON DELETE CASCADE,
  CONSTRAINT ck_password_token_purpose CHECK (purpose IN ('Reset', 'Invite'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Personal API tokens for tools (e.g. the KAEK importer sends "Authorization: Bearer nad_..."). The token acts as its
-- user with the user's permissions; only its SHA-256 is stored. Revoked by the Admin, or with the user's deactivation.
CREATE TABLE api_token (
  api_token_id BIGINT NOT NULL AUTO_INCREMENT,
  user_id BIGINT NOT NULL,
  name VARCHAR(100) NOT NULL,
  token_hash CHAR(64) NOT NULL,
  token_prefix VARCHAR(12) NOT NULL COMMENT 'first characters, to recognise the token in lists',
  created_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  last_used_utc DATETIME(3) NULL,
  expires_utc DATETIME(3) NULL,
  revoked_utc DATETIME(3) NULL,
  PRIMARY KEY (api_token_id),
  UNIQUE KEY uq_api_token_hash (token_hash),
  KEY ix_api_token_user (user_id),
  CONSTRAINT fk_api_token_user FOREIGN KEY (user_id) REFERENCES app_user (user_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- ASP.NET Data Protection keys (they sign the sign-in cookie). Kept in the DB so every app instance
-- (EC2/Fargate) accepts the same cookies and restarts don't sign everyone out.
CREATE TABLE data_protection_key (
  friendly_name VARCHAR(200) NOT NULL,
  xml MEDIUMTEXT NOT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (friendly_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

ALTER TABLE activity ADD KEY ix_activity_user (user_id);

-- ---------------------------------------------------------------------------------------------------------------
-- Catalog
-- ---------------------------------------------------------------------------------------------------------------

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order) VALUES
  ('VIEW_ALL_PARCELS', 'See all Parcels', 'Every Parcel on the map. Without it: only Parcels of visible Assets or granted Parcels.', 'Role', NULL, 'Parcels', 10),
  ('EDIT_ALL_PARCELS', 'Create and edit Parcels', 'Draw/edit polygons, KAEK, OT/plot, legal owners, Parcel files.', 'Role', NULL, 'Parcels', 20),
  ('VIEW_LEGAL_OWNERS', 'See legal owners', 'Legal owners of the Parcels the user can see.', 'Role', NULL, 'Parcels', 30),
  ('VIEW_ALL_ASSETS', 'See all Assets', 'Every Asset, including competing Assets on the same Parcel.', 'Role', NULL, 'Assets', 100),
  ('EDIT_ALL_ASSETS', 'Edit all Assets', 'Edit any Asset and choose any Managing Contact.', 'Role', NULL, 'Assets', 110),
  ('CREATE_ASSET', 'Create own Assets', 'Create Assets whose Managing Contact is the user''s own Contact.', 'Role', NULL, 'Assets', 120),
  ('VIEW_OWN_ASSET', 'See own Assets', 'Assets whose Managing Contact is the user''s Contact.', 'Role', NULL, 'Assets', 130),
  ('EDIT_OWN_ASSET', 'Edit own Assets', 'Edit Assets whose Managing Contact is the user''s Contact.', 'Role', NULL, 'Assets', 140),
  ('VIEW_PRICE', 'See prices', 'Ask prices of the Assets the user can see (own and editable Assets always show theirs).', 'Role', NULL, 'Assets', 150),
  ('VIEW_ALL_PORTFOLIOS', 'See all Portfolios', 'Every Portfolio and the Assets in it.', 'Role', NULL, 'Portfolios', 200),
  ('MANAGE_PORTFOLIOS', 'Manage Portfolios', 'Create Portfolios; edit any Portfolio and its Assets.', 'Role', NULL, 'Portfolios', 210),
  ('VIEW_ALL_CONTACTS', 'See all Contacts', 'Search and open every Contact.', 'Role', NULL, 'Contacts', 300),
  ('MANAGE_CONTACTS', 'Manage Contacts', 'Create and edit Contacts and their files.', 'Role', NULL, 'Contacts', 310),
  ('VIEW_LEGAL_FILES', 'See Legal files', 'Files whose type is in the Legal category.', 'Role', NULL, 'Files', 400),
  ('VIEW_ENGINEERING_FILES', 'See Engineering files', 'Files whose type is in the Engineering category.', 'Role', NULL, 'Files', 410),
  ('VIEW_MARKETING_FILES', 'See Marketing files', 'Photos, videos, drone media (Marketing category).', 'Role', NULL, 'Files', 420),
  ('VIEW_CADASTRAL_FILES', 'See Cadastral files', 'Files whose type is in the Cadastral category.', 'Role', NULL, 'Files', 430),
  ('VIEW_PERMISSION_FILES', 'See Permission files', 'Signed permissions (Permission category).', 'Role', NULL, 'Files', 440),
  ('VIEW_GENERAL_FILES', 'See General files', 'Other documents (General category).', 'Role', NULL, 'Files', 450),
  ('UPLOAD_OWN_ASSET_FILE', 'Upload to own Assets', 'Upload files to Assets whose Managing Contact is the user''s Contact.', 'Role', NULL, 'Files', 460),
  ('UPLOAD_ASSIGNED_ASSET_FILE', 'Upload to granted Assets', 'Upload files to Assets the user was granted access to.', 'Role', NULL, 'Files', 470),
  ('MANAGE_USERS', 'Manage users and access', 'Create/edit/delete users, reset passwords, roles, grant/revoke access.', 'Role', NULL, 'Administration', 500),
  ('MANAGE_METADATA', 'Manage lookup lists', 'Statuses, property/portfolio/file types, contact roles, areas.', 'Role', NULL, 'Administration', 510),
  ('VIEW_ASSET', 'View this Asset', 'Open the Asset (files by the role''s file categories).', 'Resource', 'Asset', 'Grants', 600),
  ('EDIT_ASSET', 'Edit this Asset', 'Edit the Asset, its professionals and files. Includes view.', 'Resource', 'Asset', 'Grants', 610),
  ('VIEW_PARCEL', 'View this Parcel', 'See the Parcel on the map.', 'Resource', 'Parcel', 'Grants', 620),
  ('EDIT_PARCEL', 'Edit this Parcel', 'Edit the Parcel, legal owners and files. Includes view.', 'Resource', 'Parcel', 'Grants', 630),
  ('VIEW_PORTFOLIO', 'View this Portfolio', 'See the Portfolio and every Asset in it.', 'Resource', 'Portfolio', 'Grants', 640),
  ('EDIT_PORTFOLIO', 'Edit this Portfolio', 'Rename, add/remove/reorder its Assets, files. Includes view.', 'Resource', 'Portfolio', 'Grants', 650),
  ('VIEW_CONTACT', 'View this Contact', 'Open the Contact.', 'Resource', 'Contact', 'Grants', 660),
  ('EDIT_CONTACT', 'Edit this Contact', 'Edit the Contact and its files. Includes view.', 'Resource', 'Contact', 'Grants', 670);

INSERT INTO security_role (code, name, description, is_system, sort_order) VALUES
  ('ADMIN', 'Admin', 'Sees everything and does everything (Appendix 1 §2.1).', 1, 10),
  ('GLOBAL_VIEWER', 'Global viewer', 'Sees all data, cannot change it (Appendix 1 §2.2).', 0, 20),
  ('AGENT', 'Agent / Mediator', 'Creates and edits own Assets, uploads to them; does not see competing Assets.', 0, 30),
  ('SALES', 'Sales', 'Sees all Assets and prices, builds Portfolios.', 0, 40),
  ('ENGINEER', 'Engineer', 'Granted Assets only; engineering/cadastral files.', 0, 50),
  ('ATTORNEY', 'Attorney', 'Granted Assets only; legal files and legal owners.', 0, 60),
  ('BUYER', 'Buyer / Customer', 'Granted Portfolios/Assets only; marketing files and prices.', 0, 70),
  ('SELLER', 'Seller', 'Granted or own Assets; marketing and legal files.', 0, 80),
  ('VIEWER', 'Viewer', 'Granted objects only; marketing and general files.', 0, 90);

-- ADMIN needs no rows: it bypasses permission checks.
INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON (
     (r.code = 'GLOBAL_VIEWER' AND p.code IN ('VIEW_ALL_PARCELS', 'VIEW_LEGAL_OWNERS', 'VIEW_ALL_ASSETS', 'VIEW_PRICE',
        'VIEW_ALL_PORTFOLIOS', 'VIEW_ALL_CONTACTS', 'VIEW_LEGAL_FILES', 'VIEW_ENGINEERING_FILES', 'VIEW_MARKETING_FILES',
        'VIEW_CADASTRAL_FILES', 'VIEW_PERMISSION_FILES', 'VIEW_GENERAL_FILES'))
  OR (r.code = 'AGENT' AND p.code IN ('VIEW_ALL_PARCELS', 'CREATE_ASSET', 'VIEW_OWN_ASSET', 'EDIT_OWN_ASSET',
        'UPLOAD_OWN_ASSET_FILE', 'VIEW_MARKETING_FILES', 'VIEW_GENERAL_FILES', 'VIEW_CADASTRAL_FILES', 'VIEW_PERMISSION_FILES'))
  OR (r.code = 'SALES' AND p.code IN ('VIEW_ALL_PARCELS', 'VIEW_ALL_ASSETS', 'VIEW_PRICE', 'VIEW_ALL_PORTFOLIOS',
        'MANAGE_PORTFOLIOS', 'VIEW_ALL_CONTACTS', 'VIEW_MARKETING_FILES', 'VIEW_GENERAL_FILES', 'VIEW_CADASTRAL_FILES'))
  OR (r.code = 'ENGINEER' AND p.code IN ('VIEW_ENGINEERING_FILES', 'VIEW_CADASTRAL_FILES', 'VIEW_PERMISSION_FILES',
        'UPLOAD_ASSIGNED_ASSET_FILE'))
  OR (r.code = 'ATTORNEY' AND p.code IN ('VIEW_LEGAL_FILES', 'VIEW_LEGAL_OWNERS', 'UPLOAD_ASSIGNED_ASSET_FILE'))
  OR (r.code = 'BUYER' AND p.code IN ('VIEW_PRICE', 'VIEW_MARKETING_FILES', 'VIEW_GENERAL_FILES'))
  OR (r.code = 'SELLER' AND p.code IN ('VIEW_OWN_ASSET', 'VIEW_PRICE', 'VIEW_MARKETING_FILES', 'VIEW_GENERAL_FILES', 'VIEW_LEGAL_FILES'))
  OR (r.code = 'VIEWER' AND p.code IN ('VIEW_MARKETING_FILES', 'VIEW_GENERAL_FILES'))
);

-- First Admin (user request 2026-09-27). No password: signs in with Google once Nadlan:Auth:Google is configured,
-- or gets a password with .\user-password.ps1 -Email oferdig2@gmail.com.
INSERT INTO app_user (email, display_name, security_role_id)
SELECT 'oferdig2@gmail.com', 'Ofer', security_role_id FROM security_role WHERE code = 'ADMIN';
