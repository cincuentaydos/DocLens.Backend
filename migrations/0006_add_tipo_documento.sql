-- tipo_documento: what ClassifyDocument decided (Invoice, Contract, Report,
-- Cv — Models/DocumentType.cs). Until now it only lived in the Extraction
-- execution's state, so the reviewer couldn't see which field template
-- SemanticExtraction applied. Written alongside campos at PENDING_REVIEW.
ALTER TABLE documento_versiones ADD COLUMN tipo_documento text;
