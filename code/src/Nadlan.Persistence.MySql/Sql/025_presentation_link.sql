-- 025: public presentation links (owner, 2026-11-05) - as the contractor's link: no sign-in, the link is the access; an
-- optional password; it lasts 1 week by default (staff may extend it or choose its last day, up to 1 year) and can be
-- disabled or replaced. A link made from a Portfolio follows the Portfolio; one made from chosen Assets keeps those.
-- Either way only Assets still For sale are shown, as their creator may present them. New table and permission only.

CREATE TABLE presentation_link (
  presentation_link_id BIGINT NOT NULL AUTO_INCREMENT,
  portfolio_id BIGINT NULL COMMENT 'a Portfolio (followed live), or NULL with asset_ids',
  asset_ids VARCHAR(1200) NULL COMMENT 'chosen Assets in their order, comma separated (at most 100)',
  title VARCHAR(300) NULL COMMENT 'what the customer reads at the top; NULL = "4 properties · Skroponeria"',
  link_token_hash CHAR(64) NOT NULL COMMENT 'SHA-256 hex of the link token',
  link_token_sealed VARCHAR(1000) NOT NULL COMMENT 'the token, sealed with data protection (for Copy link)',
  link_enabled TINYINT(1) NOT NULL DEFAULT 1,
  link_expires_utc DATETIME(3) NOT NULL COMMENT 'creation + 1 week by default; at most a year ahead',
  password_hash VARCHAR(255) NULL COMMENT 'NULL = no password',
  password_version INT NOT NULL DEFAULT 0 COMMENT 'bumped on every change: forgets the phones that gave the old one',
  failed_unlocks INT NOT NULL DEFAULT 0,
  locked_until_utc DATETIME(3) NULL COMMENT 'after 10 wrong passwords: locked for 15 minutes',
  open_count INT NOT NULL DEFAULT 0,
  last_opened_utc DATETIME(3) NULL,
  created_by_user_id BIGINT NOT NULL COMMENT 'the link shows what this user may present; an inactive user ends it',
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (presentation_link_id),
  UNIQUE KEY uq_presentation_link_token (link_token_hash),
  KEY ix_presentation_link_portfolio (portfolio_id),
  CONSTRAINT fk_presentation_link_portfolio FOREIGN KEY (portfolio_id) REFERENCES portfolio (portfolio_id) ON DELETE CASCADE,
  CONSTRAINT fk_presentation_link_user FOREIGN KEY (created_by_user_id) REFERENCES app_user (user_id),
  CONSTRAINT ck_presentation_link_target CHECK ((portfolio_id IS NULL) <> (asset_ids IS NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'SHARE_PRESENTATION', 'Public presentation links', 'Send a presentation as a link that opens without signing in.', 'Role', NULL, 'Pages', 5
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'SHARE_PRESENTATION');

-- Admin has every permission already; the Data person gets it (owner, 2026-11-05: "for now admin and data person").
INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code = 'SHARE_PRESENTATION'
WHERE r.code = 'DATA_PERSON'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);
