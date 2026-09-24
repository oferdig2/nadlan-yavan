-- 004: File types + one generic attachment table for documents, photos, videos, drone media, signed papers.
-- Bytes live in S3 (browser uploads directly, multipart); this table holds only metadata.

CREATE TABLE file_type (
  file_type_id INT NOT NULL AUTO_INCREMENT,
  code VARCHAR(40) NOT NULL,
  name VARCHAR(100) NOT NULL,
  category VARCHAR(20) NOT NULL COMMENT 'Legal | Engineering | Marketing | Cadastral | Permission | General - drives file visibility later',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  sort_order INT NOT NULL DEFAULT 0,
  PRIMARY KEY (file_type_id),
  UNIQUE KEY uq_file_type_code (code),
  CONSTRAINT ck_file_type_category CHECK (category IN ('Legal', 'Engineering', 'Marketing', 'Cadastral', 'Permission', 'General'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Legacy Airtable document columns + media, mapped to categories. Editable later in Admin.
INSERT INTO file_type (code, name, category, sort_order) VALUES
  ('PHOTO', 'Photo', 'Marketing', 10),
  ('DRONE_PHOTO', 'Drone photo', 'Marketing', 20),
  ('VIDEO', 'Video', 'Marketing', 30),
  ('DRONE_VIDEO', 'Drone video', 'Marketing', 40),
  ('TABO', 'TABO / Land registry certificate', 'Legal', 100),
  ('TITLE', 'Title deed', 'Legal', 110),
  ('REGISTRY_REPORT', 'Registry report', 'Legal', 120),
  ('PROPERTY_TAX', 'Property tax payment', 'Legal', 130),
  ('MANDATE', 'Mandate', 'Legal', 140),
  ('SELLER_PLOT_PLAN', 'Seller plot plan', 'Engineering', 200),
  ('BUYER_PLOT_PLAN', 'Buyer plot plan', 'Engineering', 210),
  ('BUYER_ENGINEER_REPORT', 'Buyer engineer report', 'Engineering', 220),
  ('LEGALIZATION_DOCS', 'Legalization docs', 'Engineering', 230),
  ('FLOOR_PLANS', 'Floor plans', 'Engineering', 240),
  ('BUILDING_LICENSE', 'Building license', 'Engineering', 250),
  ('DIGITAL_ID', 'Digital ID', 'Cadastral', 300),
  ('KHD_ECD', 'KHD / ECD', 'Cadastral', 310),
  ('CADASTRAL_PLAN', 'Cadastral plan', 'Cadastral', 320),
  ('SIGNED_PERMISSION', 'Signed permission', 'Permission', 400),
  ('OTHER', 'Other document', 'General', 900);

CREATE TABLE file_attachment (
  file_attachment_id BIGINT NOT NULL AUTO_INCREMENT,
  file_type_id INT NOT NULL,
  attached_to_type VARCHAR(20) NOT NULL COMMENT 'Parcel | Asset | Portfolio | Contact | User',
  attached_to_id BIGINT NOT NULL,
  storage_key VARCHAR(700) NOT NULL COMMENT 'S3 object key',
  original_file_name VARCHAR(255) NOT NULL,
  mime_type VARCHAR(150) NOT NULL,
  file_size BIGINT NOT NULL,
  caption VARCHAR(300) NULL,
  notes TEXT NULL,
  sort_order INT NULL,
  upload_status VARCHAR(20) NOT NULL DEFAULT 'Pending' COMMENT 'Pending = bytes still uploading; Ready = usable',
  s3_upload_id VARCHAR(1024) NULL COMMENT 'multipart upload id while Pending',
  uploaded_by_user_id BIGINT NULL,
  uploaded_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  completed_utc DATETIME(3) NULL,
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (file_attachment_id),
  UNIQUE KEY uq_file_attachment_storage_key (storage_key),
  KEY ix_file_attachment_target (attached_to_type, attached_to_id, upload_status),
  CONSTRAINT fk_file_attachment_type FOREIGN KEY (file_type_id) REFERENCES file_type (file_type_id),
  CONSTRAINT ck_file_attachment_target CHECK (attached_to_type IN ('Parcel', 'Asset', 'Portfolio', 'Contact', 'User')),
  CONSTRAINT ck_file_attachment_status CHECK (upload_status IN ('Pending', 'Ready'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
