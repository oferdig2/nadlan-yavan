-- 003: Contacts, reference lists, Assets (business layer over Parcels), Portfolios.
-- Portfolio has no price in V1 (decided 2026-09-24).

CREATE TABLE contact_role (
  contact_role_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (contact_role_id),
  UNIQUE KEY uq_contact_role_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO contact_role (code, name, sort_order) VALUES
  ('AGENT', 'Agent', 10), ('CUSTOMER', 'Customer', 20), ('ENGINEER', 'Engineer', 30), ('ATTORNEY', 'Attorney', 40),
  ('TOPOGRAPHER', 'Topographer', 50), ('LEGAL_OWNER', 'Legal Owner', 60),
  ('COMPANY_REPRESENTATIVE', 'Company Representative', 70), ('OTHER', 'Other', 80);

CREATE TABLE contact (
  contact_id BIGINT NOT NULL AUTO_INCREMENT,
  contact_type VARCHAR(20) NOT NULL DEFAULT 'Person' COMMENT 'Person | Organization',
  display_name VARCHAR(200) NOT NULL,
  first_name VARCHAR(100) NULL,
  last_name VARCHAR(100) NULL,
  company_name VARCHAR(200) NULL,
  email VARCHAR(320) NULL,
  phone VARCHAR(50) NULL,
  cell_phone VARCHAR(50) NULL,
  notes TEXT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (contact_id),
  KEY ix_contact_display_name (display_name),
  KEY ix_contact_email (email),
  CONSTRAINT ck_contact_type CHECK (contact_type IN ('Person', 'Organization'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE contact_contact_role (
  contact_id BIGINT NOT NULL,
  contact_role_id INT NOT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (contact_id, contact_role_id),
  KEY ix_contact_contact_role_role (contact_role_id),
  CONSTRAINT fk_ccr_contact FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
  CONSTRAINT fk_ccr_role FOREIGN KEY (contact_role_id) REFERENCES contact_role (contact_role_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE asset_status (
  asset_status_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL,
  map_color CHAR(7) NOT NULL DEFAULT '#2563eb' COMMENT 'polygon colour in the Assets view',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (asset_status_id),
  UNIQUE KEY uq_asset_status_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- From the legacy "For Sale status" values plus the end states the business needs; editable later in Admin.
INSERT INTO asset_status (code, name, map_color, sort_order) VALUES
  ('FOR_SALE', 'For sale', '#16a34a', 10), ('NOT_FOR_SALE', 'Not for sale', '#64748b', 20),
  ('SOLD', 'Sold', '#dc2626', 30), ('WITHDRAWN', 'Withdrawn', '#a16207', 40);

CREATE TABLE property_type (
  property_type_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (property_type_id),
  UNIQUE KEY uq_property_type_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO property_type (code, name, sort_order) VALUES
  ('PLOT', 'Plot', 10), ('HOUSE', 'House', 20), ('APARTMENT', 'Apartment', 30), ('BUILDING', 'Building', 40),
  ('SKELETON', 'Skeleton', 50);

CREATE TABLE asset (
  asset_id BIGINT NOT NULL AUTO_INCREMENT,
  managing_contact_id BIGINT NOT NULL,
  property_type_id INT NULL,
  asset_status_id INT NOT NULL,
  ask_price DECIMAL(14,2) NULL,
  currency_code CHAR(3) NULL,
  house_sqm DECIMAL(10,2) NULL,
  special_conditions TEXT NULL,
  remarks TEXT NULL,
  is_exclusive TINYINT(1) NULL,
  created_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (asset_id),
  KEY ix_asset_managing_contact (managing_contact_id),
  KEY ix_asset_status (asset_status_id),
  KEY ix_asset_price (ask_price),
  CONSTRAINT fk_asset_managing_contact FOREIGN KEY (managing_contact_id) REFERENCES contact (contact_id),
  CONSTRAINT fk_asset_property_type FOREIGN KEY (property_type_id) REFERENCES property_type (property_type_id),
  CONSTRAINT fk_asset_status FOREIGN KEY (asset_status_id) REFERENCES asset_status (asset_status_id),
  CONSTRAINT ck_asset_currency_with_price CHECK (ask_price IS NULL OR currency_code IS NOT NULL),
  CONSTRAINT ck_asset_price_non_negative CHECK (ask_price IS NULL OR ask_price >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Data model allows several Parcels per Asset; Phase 1 UX creates exactly one.
CREATE TABLE asset_parcel (
  asset_id BIGINT NOT NULL,
  parcel_id BIGINT NOT NULL,
  sort_order INT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (asset_id, parcel_id),
  KEY ix_asset_parcel_parcel (parcel_id),
  CONSTRAINT fk_asset_parcel_asset FOREIGN KEY (asset_id) REFERENCES asset (asset_id),
  CONSTRAINT fk_asset_parcel_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE portfolio_type (
  portfolio_type_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (portfolio_type_id),
  UNIQUE KEY uq_portfolio_type_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO portfolio_type (code, name, sort_order) VALUES
  ('SHOWCASE', 'Showcase', 10), ('DEAL', 'Deal', 20), ('TRANSACTION', 'Transaction', 30), ('HOLDINGS', 'Holdings', 40);

CREATE TABLE portfolio (
  portfolio_id BIGINT NOT NULL AUTO_INCREMENT,
  name VARCHAR(200) NOT NULL,
  portfolio_type_id INT NOT NULL,
  description TEXT NULL,
  created_by_user_id BIGINT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (portfolio_id),
  KEY ix_portfolio_name (name),
  CONSTRAINT fk_portfolio_type FOREIGN KEY (portfolio_type_id) REFERENCES portfolio_type (portfolio_type_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Explicit membership only: filters help select Assets but never add them later by themselves.
CREATE TABLE portfolio_asset (
  portfolio_id BIGINT NOT NULL,
  asset_id BIGINT NOT NULL,
  sort_order INT NULL,
  added_by_user_id BIGINT NULL,
  added_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (portfolio_id, asset_id),
  KEY ix_portfolio_asset_asset (asset_id),
  CONSTRAINT fk_portfolio_asset_portfolio FOREIGN KEY (portfolio_id) REFERENCES portfolio (portfolio_id),
  CONSTRAINT fk_portfolio_asset_asset FOREIGN KEY (asset_id) REFERENCES asset (asset_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
