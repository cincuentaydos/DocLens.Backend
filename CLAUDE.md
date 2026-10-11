# DocLens — Proyecto 52

Plataforma multi-tenant de IA para la gestión de expedientes jurídicos sobre AWS Serverless.
Un despacho (tenant) centraliza sus Procesos (expedientes), Clientes y Documentos, y usa IA para clasificar documentos, extraer datos, resumir, responder preguntas contextuales sobre un expediente y redactar primeras versiones de documentos/comunicaciones. Ver `DocLens.Docs` (ADR-001…014) para la arquitectura completa.

## Norma de idioma

- **Documentación en español**: READMEs, `docs/`, ADRs, este fichero y los `CLAUDE.md`, y las descripciones de pull requests.
- **Código en inglés**: código, identificadores (clases, métodos, variables, recursos y variables de Terraform), nombres de ficheros de código, comentarios en el código y mensajes de commit.
- Los términos técnicos se mantienen en su forma original (p. ej. *presigned URL*, *event source mapping*, *Data API*).

## Nomenclatura

Fuente de verdad: [`docs/glosario.md`](docs/glosario.md) (término de negocio en español ↔ término en código en inglés).

- **Estructura y estado del sistema en inglés**: tablas, columnas, restricciones, índices, tabla de control de migraciones y valores de estado del sistema (escaneo de malware y procesamiento: `PENDING`, `CLEAN`, `PROCESSED`, `REJECTED`). Esquema actual (`DocLens.Infra/migrations/002_documents.sql`):
  - `documents`: `id`, `tenant_id`, `latest_version`, `created_at`, `updated_at`.
  - `document_versions`: `id`, `document_id`, `version_number`, `s3_key`, `content_type`, `status`, `created_at`, `updated_at`.
- **Contenido de negocio en español como dato**: el contenido extraído de los documentos y los valores de dominio que ve el usuario (p. ej. tipos de documento `contrato`, `escrito`) son datos, no esquema. Las claves JSON de la extracción van en inglés; los valores conservan el idioma original del documento.
- **Código en inglés** (clases, métodos, variables, parámetros SQL, comentarios, commits) y **documentación en español**.
- Pendiente de decisión, no tocar sin acordarlo: atributos de Cognito `custom:tipoUsuario`/`custom:clienteId` (`custom:tenantId` ya está en inglés), la tabla `kb.embeddings` (`chunk_texto`), el rediseño del enum `DocumentType` y el de los estados de ADR-011.
- Cambiar un nombre persistido exige una migración nueva en `DocLens.Infra/migrations` y el cambio de código en el mismo ciclo.

## Stack tecnológico

| Capa | Tecnología |
|---|---|
| Cómputo | AWS Lambda (.NET 10, runtime `provided.al2023`, publicación self-contained) — un único binario para las Lambdas de API, Processor y OCR result (ADR-010) |
| Entrada del API | Amazon API Gateway (HTTP API v2), ruta `$default` con autorizador JWT; rutas de negocio bajo `/api` |
| Identidad | Amazon Cognito (JWT con claims `custom:tenantId`, `custom:tipoUsuario`, `custom:clienteId`) |
| Extracción de texto | Amazon Textract `DetectDocumentText` síncrono (una página) en `TextractOcrService`. **Aún no implementado**: PdfPig para PDF digital, parseo nativo de `.md`/`.docx`/`.pptx`/`.xlsx` y el flujo asíncrono `StartDocumentTextDetection` (ADR-003) |
| Análisis IA | Amazon Bedrock (Claude vía `InvokeModel`, perfil de inferencia `CHAT_MODEL_ID`); sin agente autónomo (ADR-012) |
| RAG | Amazon Bedrock Knowledge Bases, con Aurora + pgvector como almacén vectorial (ADR-001/008) — infraestructura en DocLens.Infra, aún sin uso desde el código |
| BD operacional + vectorial | Amazon Aurora PostgreSQL Serverless v2 + pgvector, accedida vía RDS Data API (ADR-008). **No se usa DynamoDB** |
| Almacenamiento de documentos | Amazon S3 (versionado, SSE-KMS) |
| Disparador del procesamiento | GuardDuty Malware Protection → EventBridge → SQS → Lambda Processor (ADR-011) |
| Edge | Route 53 + CloudFront + WAF + Shield Standard + ACM (ADR-009) |
| Observabilidad | Amazon CloudWatch (logs JSON) + AWS X-Ray |
| IaC | Solo Terraform (HCL) — ver `infra/terraform/` (el scaffold de CDK se eliminó, ADR-002) |

## Estructura del repositorio

```
src/
  DocLens.Lambda/          # Binario único de las tres Lambdas (ver src/DocLens.Lambda/CLAUDE.md)
tests/
  DocLens.Lambda.Tests/    # Pruebas unitarias (xUnit + NSubstitute)
  bruno/                   # Colección Bruno para pruebas de humo contra sbx
infra/
  terraform/               # Terraform — único IaC (ver infra/README.md)
scripts/
  package-lambda-zip.py    # Empaqueta la publicación con `bootstrap` ejecutable
DocLens.slnx
```

## Decisiones de arquitectura

- **Diseño de dentro hacia fuera:** primero la lógica de extracción, después el cableado de infraestructura.
- **Silo lógico multi-tenant:** el `tenantId` del JWT de Cognito (`tenant_id` en Aurora) se propaga a todas las capas: prefijo S3, filas de Aurora (toda consulta filtra por `tenant_id`), filtro de metadatos de la Knowledge Base y logs.
- **Extracción asíncrona dirigida por eventos (objetivo, ADR-011):** el resultado de GuardDuty dispara el pipeline vía EventBridge → SQS → Processor. Estado actual: el Processor solo marca la versión como `CLEAN`, y la extracción se ejecuta de forma síncrona en `POST /api/documents/process`, que **sí existe** y exige que la versión sea del tenant y esté `CLEAN`/`PROCESSED`.
- **Sin agente autónomo:** el flujo de IA es explícito y orquestado por el backend — autorizar → recuperar de la Knowledge Base → generar con Claude → devolver con fuentes citadas (ADR-012).
- **Entornos:** solo `sbx` y `prod`.
- **Región principal:** `eu-west-1`, una sola región en la V1 (ADR-013).

## Convenciones de código

- Records para DTOs inmutables (`ProcessDocumentRequest`, `ExtractionResult`, `DocumentKey`).
- Pares interfaz + implementación para todos los servicios (`IOcrService` / `TextractOcrService`, `IDocumentRepository` / `AuroraDocumentRepository`).
- Servicios registrados como `Scoped`; clientes del AWS SDK como `Singleton` (vía `AddAWSService<T>`).
- `TenantContext` se resuelve por petición en `TenantMiddleware` antes de ejecutar cualquier servicio.
- Logging estructurado con `ILogger<T>`: incluir siempre `TenantId`, `DocumentId` y el contexto de la operación.
- Escrituras de varias sentencias en Aurora dentro de una transacción del Data API (`BeginTransaction`/`CommitTransaction`/`RollbackTransaction`).

## Ejecución local

```bash
dotnet build
dotnet test
dotnet run --project src/DocLens.Lambda   # sin runtime de Lambda, rol "api", Swagger en Development
```

Variables de entorno: `DOCUMENT_BUCKET`, `AURORA_CLUSTER`, `AURORA_SECRET` (opcional `AURORA_DATABASE`), `CHAT_MODEL_ID` y credenciales de AWS.

## Despliegue

- **Infraestructura de este repo** (módulo `processing`: Lambdas de API/Processor/OCR result, API Gateway, SQS): Terraform en `infra/terraform/`, ver `infra/README.md` y los `tfvars` de `sbx`/`prod`. Todo lo demás (Cognito, Aurora, bucket de documentos, Knowledge Base, CloudFront/WAF/DNS y las migraciones SQL de `migrations/`) vive en el repo `DocLens.Infra`.
- **Código**: el workflow `CD` (`.github/workflows/cd.yml`) publica la build self-contained, la sube a S3 y ejecuta `UpdateFunctionCode` en las tres funciones al hacer push a `development`. Terraform ignora `s3_key`/`source_code_hash` tras la creación.
- **CI**: `.github/workflows/ci.yml` en cada pull request (`dotnet build`/`test`, `terraform fmt -check`, `terraform validate`).
- No usar `dotnet lambda deploy-function` con un ZIP normal: el runtime `provided.al2023` necesita la publicación self-contained con el ejecutable `bootstrap` (ver `infra/README.md`).
