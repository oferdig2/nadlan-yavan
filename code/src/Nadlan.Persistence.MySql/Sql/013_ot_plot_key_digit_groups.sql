-- 013: search keys keep a separator between two digit groups (review of 2026-10-07): 012 removed every separator, so
-- "47/3" and "473" became the same number and a typo "4-7" matched 47. Now spaces . / _ - BETWEEN DIGITS become one "#"
-- ("47/3", "47-3" -> 47#3; base 47, so a search for 47 still finds it), and are removed elsewhere ("47 a", "47-A" -> 47A).
-- Same rule as Nadlan.Core.Parcels.ParcelNumberKey - change both together. *_base follows (generated from *_key).

ALTER TABLE parcel
  MODIFY COLUMN ot_key VARCHAR(64) GENERATED ALWAYS AS (NULLIF(REGEXP_REPLACE(
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
      REGEXP_REPLACE(REGEXP_REPLACE(UPPER(CONCAT(COALESCE(ot, ''), COALESCE(ot_ext, ''))), '(?<=[0-9])[[:space:]./_-]+(?=[0-9])', '#'), '[[:space:]./_-]+', ''),
      'Α', 'A'), 'Β', 'B'), 'Ε', 'E'), 'Ζ', 'Z'), 'Η', 'H'), 'Ι', 'I'), 'Κ', 'K'), 'Μ', 'M'), 'Ν', 'N'), 'Ο', 'O'), 'Ρ', 'P'), 'Τ', 'T'), 'Υ', 'Y'), 'Χ', 'X'),
    '^0+([0-9])', '$1'), '')) STORED COMMENT 'search key of OT + ext (012, digit groups 013)',
  MODIFY COLUMN plot_key VARCHAR(64) GENERATED ALWAYS AS (NULLIF(REGEXP_REPLACE(
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
      REGEXP_REPLACE(REGEXP_REPLACE(UPPER(CONCAT(COALESCE(plot_number, ''), COALESCE(plot_ext, ''))), '(?<=[0-9])[[:space:]./_-]+(?=[0-9])', '#'), '[[:space:]./_-]+', ''),
      'Α', 'A'), 'Β', 'B'), 'Ε', 'E'), 'Ζ', 'Z'), 'Η', 'H'), 'Ι', 'I'), 'Κ', 'K'), 'Μ', 'M'), 'Ν', 'N'), 'Ο', 'O'), 'Ρ', 'P'), 'Τ', 'T'), 'Υ', 'Y'), 'Χ', 'X'),
    '^0+([0-9])', '$1'), '')) STORED COMMENT 'search key of plot + ext (012, digit groups 013)';
