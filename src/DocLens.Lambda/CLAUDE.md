# DocLens.Lambda

Binario único de las tres Lambdas. `Program.cs` elige el rol con `DOCLENS_FUNCTION_ROLE` (fallback: `AWS_LAMBDA_FUNCTION_NAME`):

| Rol | Qué hace |
|---|---|
| `api` | Host ASP.NET Core minimal API (HTTP API v2) con `TenantMiddleware` y las rutas bajo `/api` |
| `processor` | Consume la cola de GuardDuty (`Workers/ProcessorHandler.cs`) y pasa la versión de `PENDING` a `CLEAN` |
| `ocr_result` | Consume las notificaciones de Textract vía SNS/SQS (`Workers/OcrResultHandler.cs`); por ahora solo las registra |

Los roles SQS usan `Workers/WorkerHost.cs` (Lambda RuntimeSupport) y devuelven `SQSBatchResponse` (fallos por mensaje).

## Flujo de peticiones (rol `api`)

```
API Gateway (JWT) → TenantMiddleware → Endpoint
  POST /api/documents/prepare → DocumentUploadService → IDocumentRepository (transacción) → presigned URL
  POST /api/documents/process → DocumentAccessGuard → DocumentExtractionService
                                                        ├── TextractOcrService              (texto)
                                                        └── BedrockSemanticAnalysisService  (campos estructurados)
```

`DocumentAccessGuard` devuelve 400 (`documentId` no UUID), 403 (clave fuera del prefijo del tenant/documento), 404 (sin fila de versión) o 409 (versión aún no `CLEAN`).

## Estructura

```
Endpoints/               # Definición de rutas — un fichero por área
Models/
  Requests/              # DTOs de entrada (records)
  Responses/             # DTOs de salida (records)
  DocumentKey.cs         # Convención de claves S3 (Build/TryParse)
  DocumentVersionStatus.cs
Data/                    # IDocumentRepository + AuroraDocumentRepository (RDS Data API)
Services/
  Access/                # DocumentAccessGuard
  Extraction/            # Orquestación: IDocumentExtractionService
  Ocr/                   # Wrapper de Textract: IOcrService
  Semantic/              # Wrapper de Bedrock + BedrockResponseParser
  Upload/                # DocumentUploadService
Workers/                 # FunctionRole, WorkerHost, ProcessorHandler, OcrResultHandler
Context/                 # Resolución de tenant (TenantContext, TenantMiddleware)
Extensions/              # Registro de DI (ServiceCollectionExtensions)
```

## Añadir un endpoint

1. Añadir la ruta en el fichero correspondiente de `Endpoints/` (o crear uno nuevo para un área nueva).
2. Registrarla en `MapDocumentEndpoints` (o en una nueva extensión `Map*Endpoints` llamada desde el grupo `/api` de `Program.cs`).
3. Crear los records de petición/respuesta en `Models/`.
4. Implementar el servicio en `Services/` y, si toca datos, el acceso en `Data/`.

## Regla multi-tenant

Todo servicio que toque datos **debe** recibir `ITenantContext` por constructor e incluir `TenantId` en claves de almacenamiento, logs y respuestas.

- Clave S3: `{tenantId}/documents/{documentId}/v{versionNumber}.{extension}` — construirla y validarla siempre con `Models/DocumentKey.cs`.
- Aurora: toda consulta filtra por `empresa_id` (= `tenantId`). No se usa DynamoDB.

## Añadir un tipo de documento

1. Añadir la variante en `Models/DocumentType.cs`.
2. Añadir el `case` correspondiente en `BedrockSemanticAnalysisService.BuildPrompt` con los campos a extraer.

## Formatos y extracción

`Models/SupportedDocumentFormats.cs` define los tipos admitidos en la subida (pdf, md, txt, docx, pptx, xlsx, png, jpg, tiff). La extracción actual solo usa Textract `DetectDocumentText` (imágenes y PDF de una página); PdfPig, el parseo nativo de Office/Markdown y el flujo asíncrono de Textract están pendientes (ADR-003).

## Patrón del AWS SDK

Los servicios reciben los clientes por inyección (`IAmazonTextract`, `IAmazonBedrockRuntime`, `IAmazonS3`, `IAmazonRDSDataService`).
Nunca instanciar clientes de AWS directamente: usar siempre DI.

## Pruebas

- Pruebas unitarias en `tests/DocLens.Lambda.Tests/`, reflejando la estructura de `src/` (`Models/`, `Services/`, `Workers/`).
- Simular todas las interfaces del AWS SDK y `IDocumentRepository` con NSubstitute: nunca llamadas reales a AWS en las pruebas.
