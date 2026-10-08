-- 014: a Parcel can be divided or united (owner, 2026-10-08). "regular" for every existing Parcel. For a divided or
-- united Parcel the user types the other OT / plot numbers as free text; V1 shows it but doesn't search it (it may
-- become structured JSON later, and then searchable). The app keeps related_numbers NULL for regular Parcels.

ALTER TABLE parcel
  ADD COLUMN division_status VARCHAR(10) NOT NULL DEFAULT 'regular' COMMENT 'regular | divided | united' AFTER plot_ext,
  ADD COLUMN related_numbers VARCHAR(500) NULL COMMENT 'divided/united only: the other OT / plot numbers, free text (not searched in V1)' AFTER division_status,
  ADD CONSTRAINT ck_parcel_division_status CHECK (division_status IN ('regular', 'divided', 'united'));
