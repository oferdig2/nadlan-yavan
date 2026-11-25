-- 033: owners and sales from the Cadastre's lawyer research service (brief AI/Remarks/CodingAgent-KtimaRecords-2026-11-24.md,
-- owner's decisions of 2026-11-24/25). New tables only; nothing existing changes. 031 and 032 are taken by the deals branches.
--
-- The KAEK importer reads a KAEK in the lawyer's own session and sends it; each send is a new snapshot of that KAEK, and the
-- older ones are kept (history). A snapshot = the cadastre_fetch rows of one KAEK with the same fetched_utc: one row per
-- unit (the plot /0/0, each flat /0/1, ...), or one row with no unit for the KAEK as a whole (NotOperating, Error).
-- These are the Cadastre's records ("as listed by the Cadastre, updated weekly"), separate from parcel_legal_owner (the
-- contacts the office manages). Admin only for now.
--
-- Personal data kept (owner, 2026-11-24): names, shares, roles, and addresses from summaries (own columns, easy to drop).
-- Never kept: a person's ΑΦΜ (tax number), ID card, birth date or place, parents' names, and the PDFs. The ΑΦΜ of a company
-- or public body is kept to match it across parcels; the CHECKs below refuse one on anything else.

CREATE TABLE cadastre_fetch (
  cadastre_fetch_id BIGINT NOT NULL AUTO_INCREMENT,
  parcel_id BIGINT NULL COMMENT 'the GreekPlot Parcel with this KAEK when the record was saved (reads go by kaek)',
  kaek CHAR(12) NOT NULL,
  unit_v INT NULL COMMENT 'NULL = the KAEK as a whole (NotOperating, or an Error before any unit was read)',
  unit_h INT NULL,
  status VARCHAR(20) NOT NULL COMMENT 'Found | NotOperating | NeedsOcr | Error',
  fetched_utc DATETIME(3) NOT NULL COMMENT 'server time of the send; all rows of one snapshot share it',
  fetched_by_user_id BIGINT NULL,
  error VARCHAR(500) NULL,
  floor_text VARCHAR(50) NULL COMMENT 'the unit''s row in the property search: floor(s)',
  title_area_sqm DECIMAL(12,2) NULL COMMENT 'area in the title',
  title_number VARCHAR(30) NULL COMMENT 'number in the title',
  plot_share_text VARCHAR(30) NULL COMMENT 'share in the plot',
  address VARCHAR(300) NULL COMMENT 'the property''s address (street, number, postcode, locality, municipality)',
  PRIMARY KEY (cadastre_fetch_id),
  KEY ix_cadastre_fetch_kaek (kaek, fetched_utc),
  KEY ix_cadastre_fetch_parcel (parcel_id),
  CONSTRAINT fk_cadastre_fetch_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id) ON DELETE SET NULL,
  CONSTRAINT fk_cadastre_fetch_user FOREIGN KEY (fetched_by_user_id) REFERENCES app_user (user_id) ON DELETE SET NULL,
  CONSTRAINT ck_cadastre_fetch_status CHECK (status IN ('Found', 'NotOperating', 'NeedsOcr', 'Error')),
  CONSTRAINT ck_cadastre_fetch_unit CHECK ((unit_v IS NULL) = (unit_h IS NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The unit's current owners from the record page ("Τρέχοντες Κύριοι"), with the fetch date of their snapshot.
CREATE TABLE cadastre_owner (
  cadastre_owner_id BIGINT NOT NULL AUTO_INCREMENT,
  cadastre_fetch_id BIGINT NOT NULL,
  sort_order INT NOT NULL DEFAULT 0,
  name VARCHAR(300) NOT NULL COMMENT 'surname + first name, or the company / body name',
  share_text VARCHAR(20) NULL COMMENT 'as written, e.g. 3 / 10',
  share_decimal DECIMAL(9,6) NULL,
  is_unknown TINYINT(1) NOT NULL DEFAULT 0 COMMENT 'ΑΓΝΩΣΤΟΣ (unknown owner)',
  owner_kind VARCHAR(12) NOT NULL COMMENT 'Person | LegalEntity | Unknown',
  entity_tax_id VARCHAR(12) NULL COMMENT 'ΑΦΜ, LegalEntity only (never a person''s)',
  entity_type_text VARCHAR(100) NULL COMMENT 'e.g. ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ ΙΔΙΩΤΙΚΟΥ ΔΙΚΑΙΟΥ, when a summary says',
  address VARCHAR(300) NULL COMMENT 'only when found in a summary; kept for now (owner, 2026-11-24), own column, easy to drop',
  PRIMARY KEY (cadastre_owner_id),
  KEY ix_cadastre_owner_fetch (cadastre_fetch_id),
  KEY ix_cadastre_owner_tax (entity_tax_id),
  CONSTRAINT fk_cadastre_owner_fetch FOREIGN KEY (cadastre_fetch_id) REFERENCES cadastre_fetch (cadastre_fetch_id) ON DELETE CASCADE,
  CONSTRAINT ck_cadastre_owner_kind CHECK (owner_kind IN ('Person', 'LegalEntity', 'Unknown')),
  CONSTRAINT ck_cadastre_owner_tax CHECK (entity_tax_id IS NULL OR owner_kind = 'LegalEntity')
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Acts: from summaries (type, date, registration, values), from a summary's previous-title list (type, number, date only),
-- and the record page's old titles from before the Cadastre (f items).
CREATE TABLE cadastre_act (
  cadastre_act_id BIGINT NOT NULL AUTO_INCREMENT,
  cadastre_fetch_id BIGINT NOT NULL,
  sort_order INT NOT NULL DEFAULT 0,
  source VARCHAR(12) NOT NULL COMMENT 'Summary | OldTitle (a summary''s previous-title list) | RecordTitle (an f item)',
  reg_no VARCHAR(20) NULL COMMENT 'registration number, e.g. 5413 of 5413/2024',
  reg_year INT NULL,
  office VARCHAR(100) NULL COMMENT 'cadastral office, e.g. ΣΤΕΡΕΑΣ ΕΛΛΑΔΑΣ',
  act_type_code VARCHAR(20) NOT NULL COMMENT 'Sale | Auction | Seizure | ParentalGift | Inheritance | Other',
  act_type_text VARCHAR(200) NULL COMMENT 'the Greek text, e.g. Αγοραπωλησία',
  act_date DATE NULL,
  deed_no VARCHAR(30) NULL,
  transaction_value DECIMAL(14,2) NULL COMMENT 'the price (for a seizure: the claim)',
  objective_value DECIMAL(14,2) NULL,
  covers_units VARCHAR(300) NULL COMMENT 'the units the summary lists, e.g. 050097508005/0/1 050097508005/0/14',
  PRIMARY KEY (cadastre_act_id),
  KEY ix_cadastre_act_fetch (cadastre_fetch_id),
  CONSTRAINT fk_cadastre_act_fetch FOREIGN KEY (cadastre_fetch_id) REFERENCES cadastre_fetch (cadastre_fetch_id) ON DELETE CASCADE,
  CONSTRAINT ck_cadastre_act_source CHECK (source IN ('Summary', 'OldTitle', 'RecordTitle'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The parties to a summary's act: acquirer(s) and transferor(s), with their role, right and share (+ address, kept for now).
CREATE TABLE cadastre_act_party (
  cadastre_act_party_id BIGINT NOT NULL AUTO_INCREMENT,
  cadastre_act_id BIGINT NOT NULL,
  sort_order INT NOT NULL DEFAULT 0,
  role VARCHAR(20) NOT NULL COMMENT 'Acquirer | Transferor | Other',
  role_text VARCHAR(100) NULL COMMENT 'e.g. ΑΠΟΚΤΩΝ, ΜΕΤΑΒΙΒΑΖΩΝ / ΠΑΡΑΧΩΡΩΝ',
  comment_text VARCHAR(100) NULL COMMENT 'e.g. ΑΓΟΡΑΣΤΡΙΑ, ΠΩΛΗΤΗΣ, ΥΠΕΡΘΕΜΑΤΙΣΤΡΙΑ',
  name VARCHAR(300) NOT NULL,
  right_text VARCHAR(100) NULL COMMENT 'e.g. ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ',
  share_text VARCHAR(20) NULL,
  party_kind VARCHAR(12) NOT NULL COMMENT 'Person | LegalEntity | Unknown',
  entity_tax_id VARCHAR(12) NULL COMMENT 'ΑΦΜ, LegalEntity only (never a person''s)',
  entity_type_text VARCHAR(100) NULL,
  address VARCHAR(300) NULL COMMENT 'kept for now (owner, 2026-11-24), own column, easy to drop',
  PRIMARY KEY (cadastre_act_party_id),
  KEY ix_cadastre_act_party_act (cadastre_act_id),
  CONSTRAINT fk_cadastre_act_party_act FOREIGN KEY (cadastre_act_id) REFERENCES cadastre_act (cadastre_act_id) ON DELETE CASCADE,
  CONSTRAINT ck_cadastre_act_party_kind CHECK (party_kind IN ('Person', 'LegalEntity', 'Unknown')),
  CONSTRAINT ck_cadastre_act_party_tax CHECK (entity_tax_id IS NULL OR party_kind = 'LegalEntity')
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
