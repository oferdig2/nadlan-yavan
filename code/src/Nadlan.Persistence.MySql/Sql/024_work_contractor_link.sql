-- 024: one link per contractor (owner + Alon, 2026-11-04). The contractor opens one address and sees all his requests'
-- parcels on one map; one password; staff can disable it, give a new one, extend it or set its expiry (1 year by
-- default). Made the first time staff need it (Copy link, the link panel). The request's own link columns (021) stay
-- as they are: links already sent keep opening their one request.

CREATE TABLE work_contractor_link (
  contact_id BIGINT NOT NULL COMMENT 'the contractor',
  link_token_hash CHAR(64) NOT NULL COMMENT 'SHA-256 hex of the link token',
  link_token_sealed VARCHAR(1000) NOT NULL COMMENT 'the token, sealed with data protection',
  link_enabled TINYINT(1) NOT NULL DEFAULT 1,
  link_expires_utc DATETIME(3) NOT NULL COMMENT 'creation + 1 year by default; staff may extend or set it',
  password_hash VARCHAR(255) NULL COMMENT 'NULL = no password on the link',
  password_version INT NOT NULL DEFAULT 0 COMMENT 'bumped on every change: signs out remembered phones',
  failed_unlocks INT NOT NULL DEFAULT 0,
  locked_until_utc DATETIME(3) NULL COMMENT 'after 10 wrong passwords: locked for 15 minutes',
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (contact_id),
  UNIQUE KEY uq_work_contractor_link_token (link_token_hash),
  CONSTRAINT fk_work_contractor_link_contact FOREIGN KEY (contact_id) REFERENCES contact (contact_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
