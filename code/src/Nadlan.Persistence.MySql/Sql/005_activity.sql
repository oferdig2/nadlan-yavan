-- 005: Activity - audit/history of changes (spec §4 Activity). Not a CRM activity system.
-- user_id stays NULL until users/auth exist.

CREATE TABLE activity (
  activity_id BIGINT NOT NULL AUTO_INCREMENT,
  entity_type VARCHAR(20) NOT NULL COMMENT 'Parcel | Asset | Portfolio | Contact | File',
  entity_id BIGINT NOT NULL,
  action_type VARCHAR(40) NOT NULL COMMENT 'e.g. AssetCreated, AssetPriceChanged, FileUploaded',
  user_id BIGINT NULL,
  summary VARCHAR(500) NOT NULL,
  metadata_json JSON NULL COMMENT 'details, e.g. {"old": 120000, "new": 135000}',
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (activity_id),
  KEY ix_activity_entity (entity_type, entity_id, created_utc),
  KEY ix_activity_created (created_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
