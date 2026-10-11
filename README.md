# DocLens.Backend

Backend de DocLens (Proyecto 52): plataforma multi-tenant de IA para la gestión
de expedientes jurídicos sobre AWS Serverless. Este repositorio contiene el
código .NET 10 de las Lambdas y su infraestructura propia en Terraform.

## Estructura

```
src/DocLens.Lambda/          # Un único binario para las tres Lambdas
  Program.cs                 # Punto de entrada: decide el rol (API / Processor / OCR result)
  Endpoints/                 # Rutas HTTP (todas bajo /api)
  Services/                  # Subida, control de acceso, OCR (Textract), análisis (Bedrock)
  Data/                      # Acceso a Aurora vía RDS Data API (transacciones, filtro por tenant)
  Workers/                   # Manejadores SQS: Processor (GuardDuty) y OCR result (Textract)
tests/DocLens.Lambda.Tests/  # Pruebas unitarias (xUnit + NSubstitute)
tests/bruno/                 # Colección Bruno para pruebas de humo contra sbx
infra/terraform/             # Terraform del módulo `processing` (ver infra/README.md)
scripts/package-lambda-zip.py
```

## Un binario, tres Lambdas

El runtime `provided.al2023` siempre ejecuta el fichero `bootstrap` e ignora
el *handler*. Por eso Terraform define la variable de entorno
`DOCLENS_FUNCTION_ROLE` en cada función:

| Función                   | `DOCLENS_FUNCTION_ROLE` | Qué ejecuta                                              |
|---------------------------|-------------------------|----------------------------------------------------------|
| `doclens-api-{env}`       | `api`                   | API ASP.NET Core minimal (HTTP API v2)                   |
| `doclens-processor-{env}` | `processor`             | Consumidor SQS de resultados limpios de GuardDuty        |
| `doclens-ocr-result-{env}`| `ocr_result`            | Consumidor SQS de notificaciones de Textract (vía SNS)   |

Si la variable no existe, se deduce del nombre de la función
(`AWS_LAMBDA_FUNCTION_NAME`).

## API

Todas las rutas de negocio cuelgan de `/api`, igual que el comportamiento
`/api/*` de CloudFront (DocLens.Infra, `modules/edge`), que reenvía la ruta sin
modificarla.

- `POST /api/documents/prepare`: genera una URL prefirmada de subida (ADR-005).
  La versión se crea en una transacción del RDS Data API con incremento
  atómico de `latest_version`, siempre filtrando por `empresa_id`.
- `POST /api/documents/process`: extrae datos estructurados. Solo se permite si:
  - `documentId` es un UUID (si no, **400**);
  - `s3Key` sigue la convención `{tenantId}/documents/{documentId}/v{n}.{ext}`
    del tenant y documento del llamante (si no, **403**);
  - existe la fila en `documento_versiones` para ese tenant (si no, **404**);
  - GuardDuty ya marcó la versión como limpia (`CLEAN`/`PROCESSED`; si no, **409**).

Swagger (`/swagger`) solo se sirve en local (`Development`) o cuando Terraform
activa `enable_swagger` (sbx). En prod está prohibido por validación.

## Ejecución local

```bash
dotnet build
dotnet test
dotnet run --project src/DocLens.Lambda   # sin runtime de Lambda, rol "api"
```

Variables de entorno necesarias: `DOCUMENT_BUCKET`, `AURORA_CLUSTER`,
`AURORA_SECRET` (opcional `AURORA_DATABASE`, por defecto `doclens`),
`CHAT_MODEL_ID` y credenciales de AWS.

## Esquema de base de datos

Las tablas `documentos` y `documento_versiones` (y el esquema `kb` de la Knowledge
Base) se crean con las migraciones SQL de `DocLens.Infra/migrations`. Deben
aplicarse antes de usar la API.

## Despliegue

- **Infraestructura**: ver [`infra/README.md`](infra/README.md).
- **Código**: el workflow `CD` (`.github/workflows/cd.yml`) compila, prueba,
  empaqueta y llama a `UpdateFunctionCode` en las tres funciones al hacer push
  a `development`.
- **CI**: el workflow `CI` (`.github/workflows/ci.yml`) se ejecuta en cada pull
  request: `dotnet build`/`dotnet test`, `terraform fmt -check` y
  `terraform validate`.
