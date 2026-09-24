-- 002: Countries, geographic areas, parcels, import traceability.
-- Conventions: snake_case, BIGINT/INT auto-increment ids, DATETIME(3) in UTC, foreign keys on.
-- Geometry is WGS84 (SRID 4326). Always write/read it with 'axis-order=long-lat'.

CREATE TABLE country (
  country_id INT NOT NULL AUTO_INCREMENT,
  code CHAR(2) NOT NULL,
  name VARCHAR(100) NOT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (country_id),
  UNIQUE KEY uq_country_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO country (code, name) VALUES ('GR', 'Greece');

CREATE TABLE geographic_area (
  geographic_area_id INT NOT NULL AUTO_INCREMENT,
  country_id INT NOT NULL,
  parent_geographic_area_id INT NULL,
  code VARCHAR(16) NOT NULL,
  name VARCHAR(200) NOT NULL,
  area_type VARCHAR(50) NULL,
  geometry POLYGON NULL SRID 4326,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (geographic_area_id),
  UNIQUE KEY uq_geographic_area_code (country_id, code),
  CONSTRAINT fk_geographic_area_country FOREIGN KEY (country_id) REFERENCES country (country_id),
  CONSTRAINT fk_geographic_area_parent FOREIGN KEY (parent_geographic_area_id) REFERENCES geographic_area (geographic_area_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE parcel (
  parcel_id BIGINT NOT NULL AUTO_INCREMENT,
  country_id INT NOT NULL,
  registry_id VARCHAR(64) NULL COMMENT 'KAEK in Greece',
  registry_id_is_provisional TINYINT(1) NOT NULL DEFAULT 0 COMMENT '1 = system-invented TMP- id, real KAEK unknown',
  geographic_area_id INT NULL,
  geometry POLYGON NOT NULL SRID 4326,
  official_area_sqm DECIMAL(12,2) NULL,
  ot VARCHAR(32) NULL,
  ot_ext VARCHAR(32) NULL,
  plot_number VARCHAR(32) NULL,
  plot_ext VARCHAR(32) NULL,
  inclination DECIMAL(6,2) NULL COMMENT 'approximate slope, percent',
  build_factor DECIMAL(6,3) NULL,
  notes TEXT NULL,
  created_by_user_id BIGINT NULL COMMENT 'NULL = system/import; FK added with the user table',
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (parcel_id),
  UNIQUE KEY uq_parcel_registry (country_id, registry_id),
  KEY ix_parcel_area_ot_plot (geographic_area_id, ot, plot_number),
  SPATIAL INDEX sx_parcel_geometry (geometry),
  CONSTRAINT fk_parcel_country FOREIGN KEY (country_id) REFERENCES country (country_id),
  CONSTRAINT fk_parcel_geographic_area FOREIGN KEY (geographic_area_id) REFERENCES geographic_area (geographic_area_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE import_batch (
  import_batch_id BIGINT NOT NULL AUTO_INCREMENT,
  source VARCHAR(100) NOT NULL,
  source_file VARCHAR(500) NOT NULL,
  started_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  completed_utc DATETIME(3) NULL,
  status VARCHAR(32) NOT NULL,
  imported_by_user_id BIGINT NULL,
  summary TEXT NULL,
  PRIMARY KEY (import_batch_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE import_record (
  import_record_id BIGINT NOT NULL AUTO_INCREMENT,
  import_batch_id BIGINT NOT NULL,
  legacy_table VARCHAR(100) NOT NULL,
  legacy_record_id VARCHAR(200) NOT NULL,
  target_entity_type VARCHAR(50) NOT NULL,
  target_entity_id BIGINT NULL,
  status VARCHAR(32) NOT NULL,
  error_message TEXT NULL,
  raw_data JSON NULL,
  PRIMARY KEY (import_record_id),
  KEY ix_import_record_legacy (legacy_table, legacy_record_id),
  KEY ix_import_record_target (target_entity_type, target_entity_id),
  KEY ix_import_record_status (import_batch_id, status),
  CONSTRAINT fk_import_record_batch FOREIGN KEY (import_batch_id) REFERENCES import_batch (import_batch_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
