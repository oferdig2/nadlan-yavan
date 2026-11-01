-- 021: work requests for contractors (owner, 2026-10-31 / 2026-11-01, remarks N5 + N6). Adds only: new tables, one
-- permission, one contact role, seed rows. No existing table or row changes.
-- A work request = a list of Parcels and, per Parcel, the works to price, sent to one contractor (a Contact) through a
-- link that works without a login. The whole module's data is here (pieces 1-4), so later pieces need no migration.

-- The work types list (kept on the Work page, not in Admin). Greek names: the owner reviews them.
CREATE TABLE work_type (
  work_type_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL COMMENT 'English',
  name_el VARCHAR(100) NOT NULL COMMENT 'Greek',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (work_type_id),
  UNIQUE KEY uq_work_type_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO work_type (code, name, name_el, sort_order) VALUES
  ('CLEAN_ONLY', 'Clean only', 'Μόνο καθαρισμός', 10),
  ('CLEAR', 'Clear', 'Εκκαθάριση', 20),
  ('BURN', 'Burn', 'Κάψιμο', 30),
  ('TRUCK', 'Truck', 'Φορτηγό', 40);

-- One per contractor and list of Parcels. The link's token is never stored: only its SHA-256 (lookup) and the token
-- sealed with data protection (so staff can copy the link again).
CREATE TABLE work_request (
  work_request_id BIGINT NOT NULL AUTO_INCREMENT,
  name VARCHAR(200) NOT NULL,
  contractor_contact_id BIGINT NOT NULL,
  link_token_hash CHAR(64) NOT NULL COMMENT 'SHA-256 hex of the link token',
  link_token_sealed VARCHAR(1000) NOT NULL COMMENT 'the token, sealed with data protection',
  link_enabled TINYINT(1) NOT NULL DEFAULT 1,
  link_expires_utc DATETIME(3) NOT NULL COMMENT 'creation + 1 year; staff may extend',
  password_hash VARCHAR(255) NULL COMMENT 'NULL = no password on the link',
  password_version INT NOT NULL DEFAULT 0 COMMENT 'bumped on every change: signs out remembered phones',
  failed_unlocks INT NOT NULL DEFAULT 0,
  locked_until_utc DATETIME(3) NULL COMMENT 'after 10 wrong passwords: locked for 15 minutes',
  show_size_to_contractor TINYINT(1) NOT NULL DEFAULT 0,
  copied_from_request_id BIGINT NULL,
  created_by_user_id BIGINT NOT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_request_id),
  UNIQUE KEY uq_work_request_token (link_token_hash),
  KEY ix_work_request_contractor (contractor_contact_id),
  CONSTRAINT fk_work_request_contractor FOREIGN KEY (contractor_contact_id) REFERENCES contact (contact_id),
  CONSTRAINT fk_work_request_copied_from FOREIGN KEY (copied_from_request_id) REFERENCES work_request (work_request_id),
  CONSTRAINT fk_work_request_created_by FOREIGN KEY (created_by_user_id) REFERENCES app_user (user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The request's Parcels. status = the Parcel's own status, worked out from its works (WorkStatusRules), kept for lists.
CREATE TABLE work_request_parcel (
  work_request_id BIGINT NOT NULL,
  parcel_id BIGINT NOT NULL,
  sort_order INT NOT NULL DEFAULT 0,
  status VARCHAR(20) NOT NULL DEFAULT 'OPEN_OFFERS' COMMENT 'OPEN_OFFERS | APPROVED | IN_WORK | DONE | PAID',
  PRIMARY KEY (work_request_id, parcel_id),
  KEY ix_work_request_parcel_parcel (parcel_id),
  CONSTRAINT fk_work_request_parcel_request FOREIGN KEY (work_request_id) REFERENCES work_request (work_request_id),
  CONSTRAINT fk_work_request_parcel_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id),
  CONSTRAINT ck_work_request_parcel_status CHECK (status IN ('OPEN_OFFERS', 'APPROVED', 'IN_WORK', 'DONE', 'PAID'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One requested work on one Parcel of the request. The price is in currency_code (EUR; the UI only shows "€").
CREATE TABLE work_item (
  work_item_id BIGINT NOT NULL AUTO_INCREMENT,
  work_request_id BIGINT NOT NULL,
  parcel_id BIGINT NOT NULL,
  work_type_id INT NOT NULL,
  status VARCHAR(20) NOT NULL DEFAULT 'REQUESTED' COMMENT 'REQUESTED | OFFERED | APPROVED | IN_WORK | DONE | PAID',
  price DECIMAL(12,2) NULL,
  currency_code CHAR(3) NOT NULL DEFAULT 'EUR',
  est_start_date DATE NULL,
  offered_utc DATETIME(3) NULL,
  approved_utc DATETIME(3) NULL,
  approved_by_user_id BIGINT NULL,
  started_utc DATETIME(3) NULL,
  done_utc DATETIME(3) NULL,
  paid_utc DATETIME(3) NULL,
  paid_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_item_id),
  UNIQUE KEY uq_work_item (work_request_id, parcel_id, work_type_id),
  KEY ix_work_item_status (status),
  KEY ix_work_item_type (work_type_id),
  CONSTRAINT fk_work_item_request_parcel FOREIGN KEY (work_request_id, parcel_id) REFERENCES work_request_parcel (work_request_id, parcel_id),
  CONSTRAINT fk_work_item_type FOREIGN KEY (work_type_id) REFERENCES work_type (work_type_id),
  CONSTRAINT fk_work_item_approved_by FOREIGN KEY (approved_by_user_id) REFERENCES app_user (user_id),
  CONSTRAINT fk_work_item_paid_by FOREIGN KEY (paid_by_user_id) REFERENCES app_user (user_id),
  CONSTRAINT ck_work_item_status CHECK (status IN ('REQUESTED', 'OFFERED', 'APPROVED', 'IN_WORK', 'DONE', 'PAID'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The conversation per Parcel of a request (piece 3).
CREATE TABLE work_message (
  work_message_id BIGINT NOT NULL AUTO_INCREMENT,
  work_request_id BIGINT NOT NULL,
  parcel_id BIGINT NOT NULL,
  author_kind VARCHAR(20) NOT NULL COMMENT 'CONTRACTOR | STAFF',
  author_user_id BIGINT NULL COMMENT 'staff only',
  body TEXT NOT NULL COMMENT 'max 4000 characters (checked by the app)',
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_message_id),
  KEY ix_work_message_parcel (work_request_id, parcel_id, created_utc),
  CONSTRAINT fk_work_message_request_parcel FOREIGN KEY (work_request_id, parcel_id) REFERENCES work_request_parcel (work_request_id, parcel_id),
  CONSTRAINT fk_work_message_author FOREIGN KEY (author_user_id) REFERENCES app_user (user_id),
  CONSTRAINT ck_work_message_author CHECK (author_kind IN ('CONTRACTOR', 'STAFF'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Pictures / videos of a work (piece 3). Not file_attachment rows, so they never show in Files dialogs, presentations or
-- Map drop until staff copy one to the Parcel. upload_* = the phone's own position at upload (Samsung strips the photo's).
CREATE TABLE work_file (
  work_file_id BIGINT NOT NULL AUTO_INCREMENT,
  work_item_id BIGINT NOT NULL,
  storage_key VARCHAR(500) NOT NULL,
  original_name VARCHAR(255) NOT NULL,
  mime_type VARCHAR(100) NOT NULL,
  file_size BIGINT NOT NULL,
  upload_status VARCHAR(20) NOT NULL DEFAULT 'Pending' COMMENT 'Pending | Ready',
  s3_upload_id VARCHAR(500) NULL,
  has_thumbnail TINYINT(1) NOT NULL DEFAULT 0,
  taken_local VARCHAR(19) NULL COMMENT 'as the camera wrote it, yyyy-MM-dd HH:mm:ss, no time zone',
  camera_make VARCHAR(100) NULL,
  camera_model VARCHAR(100) NULL,
  latitude DECIMAL(10,7) NULL,
  longitude DECIMAL(10,7) NULL,
  width INT NULL,
  height INT NULL,
  duration_s DECIMAL(10,2) NULL,
  upload_latitude DECIMAL(10,7) NULL,
  upload_longitude DECIMAL(10,7) NULL,
  upload_accuracy_m DECIMAL(8,1) NULL,
  uploaded_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  copied_to_file_attachment_id BIGINT NULL COMMENT 'set by "Copy to parcel"',
  PRIMARY KEY (work_file_id),
  UNIQUE KEY uq_work_file_key (storage_key),
  KEY ix_work_file_item (work_item_id),
  KEY ix_work_file_pending (upload_status, uploaded_utc),
  CONSTRAINT fk_work_file_item FOREIGN KEY (work_item_id) REFERENCES work_item (work_item_id),
  CONSTRAINT ck_work_file_status CHECK (upload_status IN ('Pending', 'Ready'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The contractor's track while his page is open (piece 3). Kept 90 days, then deleted by the module's daily job.
CREATE TABLE work_location (
  work_location_id BIGINT NOT NULL AUTO_INCREMENT,
  work_request_id BIGINT NOT NULL,
  latitude DECIMAL(10,7) NOT NULL,
  longitude DECIMAL(10,7) NOT NULL,
  accuracy_m DECIMAL(8,1) NULL,
  recorded_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_location_id),
  KEY ix_work_location_request (work_request_id, recorded_utc),
  KEY ix_work_location_recorded (recorded_utc),
  CONSTRAINT fk_work_location_request FOREIGN KEY (work_request_id) REFERENCES work_request (work_request_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The request's history. Its own table on purpose: the module doesn't write into the shared activity log (isolation).
-- No foreign keys to the Parcel or work: the history keeps a line about a Parcel or work that was removed since.
CREATE TABLE work_event (
  work_event_id BIGINT NOT NULL AUTO_INCREMENT,
  work_request_id BIGINT NOT NULL,
  parcel_id BIGINT NULL,
  work_item_id BIGINT NULL,
  actor_kind VARCHAR(20) NOT NULL COMMENT 'CONTRACTOR | STAFF | SYSTEM',
  actor_user_id BIGINT NULL,
  action VARCHAR(40) NOT NULL,
  summary VARCHAR(1000) NOT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_event_id),
  KEY ix_work_event_request (work_request_id, created_utc),
  CONSTRAINT fk_work_event_request FOREIGN KEY (work_request_id) REFERENCES work_request (work_request_id),
  CONSTRAINT fk_work_event_actor FOREIGN KEY (actor_user_id) REFERENCES app_user (user_id),
  CONSTRAINT ck_work_event_actor CHECK (actor_kind IN ('CONTRACTOR', 'STAFF', 'SYSTEM'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'MANAGE_WORK', 'Work requests', 'Create work requests for contractors, approve prices, mark paid.', 'Role', NULL, 'Pages', 4
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'MANAGE_WORK');

-- Admin has every permission already; the Data person gets it.
INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code = 'MANAGE_WORK'
WHERE r.code = 'DATA_PERSON'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);

-- The contractors' list = Contacts with this role. Created by the build so it always exists (owner, 2026-11-01).
INSERT INTO contact_role (code, name, sort_order)
SELECT 'CONTRACTOR', 'Contractor', (SELECT COALESCE(MAX(x.sort_order), 0) + 10 FROM (SELECT sort_order FROM contact_role) x)
WHERE NOT EXISTS (SELECT 1 FROM contact_role WHERE code = 'CONTRACTOR');
