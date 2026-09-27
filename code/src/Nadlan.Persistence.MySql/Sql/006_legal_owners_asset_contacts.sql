-- 006: Legal Owners of Parcels, and professionals linked to Assets (spec §4 ParcelLegalOwner, AssetContact).
-- Legal ownership belongs to the Parcel; it never changes an Asset's Managing Contact (Scenario 8).

CREATE TABLE parcel_legal_owner (
  parcel_id BIGINT NOT NULL,
  contact_id BIGINT NOT NULL,
  ownership_percent DECIMAL(6,3) NULL COMMENT 'optional, 0 < p <= 100',
  notes TEXT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (parcel_id, contact_id),
  KEY ix_parcel_legal_owner_contact (contact_id),
  CONSTRAINT fk_plo_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id),
  CONSTRAINT fk_plo_contact FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
  CONSTRAINT ck_plo_percent CHECK (ownership_percent IS NULL OR (ownership_percent > 0 AND ownership_percent <= 100))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The Managing Contact is NOT stored here (it is asset.managing_contact_id).
CREATE TABLE asset_contact (
  asset_contact_id BIGINT NOT NULL AUTO_INCREMENT,
  asset_id BIGINT NOT NULL,
  contact_id BIGINT NOT NULL,
  relationship_type VARCHAR(30) NOT NULL COMMENT 'Engineer | Attorney | Topographer | CustomerContact | Other',
  notes TEXT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (asset_contact_id),
  UNIQUE KEY uq_asset_contact (asset_id, contact_id, relationship_type),
  KEY ix_asset_contact_contact (contact_id),
  CONSTRAINT fk_asset_contact_asset FOREIGN KEY (asset_id) REFERENCES asset (asset_id),
  CONSTRAINT fk_asset_contact_contact FOREIGN KEY (contact_id) REFERENCES contact (contact_id),
  CONSTRAINT ck_asset_contact_type CHECK (relationship_type IN ('Engineer', 'Attorney', 'Topographer', 'CustomerContact', 'Other'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
