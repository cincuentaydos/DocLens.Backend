namespace DocLens.Lambda.Services.Upload;

public class UnsupportedContentTypeException(string contentType)
    : Exception($"Unsupported content type: {contentType}");

public class DocumentNotFoundException(string documentId)
    : Exception($"Document not found: {documentId}");

public class InvalidDocumentIdException(string documentId)
    : Exception($"documentId must be a UUID: {documentId}");
