# Infraestructura

Terraform propio de este backend: el módulo `processing` (Lambda de API,
Lambda Processor, Lambda de resultado OCR asíncrono, API Gateway, SQS/DLQ).
Todo lo demás que necesita DocLens (Cognito, Aurora, el bucket de documentos,
la Knowledge Base de Bedrock, CloudFront/WAF/DNS) vive en el repositorio
[`DocLens.Infra`](https://github.com/cincuentaydos/DocLens.Infra). Ver
[ADR-002](../DocLens.Docs/docs/adrs/002-iac-strategy.md) para el motivo de
mantener `processing` aquí.

Dos entornos: `sbx` y `prod`, en la misma cuenta de AWS que `DocLens.Infra`.

## Estructura

```
terraform/
  main.tf            # módulo raíz — conecta modules/processing y lee las salidas
                     # de DocLens.Infra mediante terraform_remote_state
  variables.tf
  outputs.tf
  envs/
    sbx.tfvars
    prod.tfvars
  modules/
    processing/      # SQS+DLQ, disparador GuardDuty→EventBridge (ADR-011), Lambda de API + HTTP API, Lambda Processor, Lambda de resultado OCR
```

## Conexión entre repositorios

`processing` necesita el bucket de documentos, el clúster Aurora, la clave KMS
y el User Pool de Cognito que crea `DocLens.Infra`. `main.tf` los lee con
`data.terraform_remote_state.infra` (mismo bucket de estado, otra clave). En
sentido contrario, el módulo `edge` de `DocLens.Infra` lee la salida
`api_endpoint` de este módulo para el origen de CloudFront, y `governance/`
lee `api_lambda_arn`, `processor_lambda_arn`, `ocr_result_lambda_arn` y
`artifacts_bucket_arn` para el rol de despliegue de GitHub Actions. Ver
`DocLens.Infra/README.md` para el orden del primer despliegue.

## Variables relevantes

| Variable             | Descripción                                                                  |
|----------------------|------------------------------------------------------------------------------|
| `environment`        | `sbx` o `prod`.                                                              |
| `lambda_zip_path`    | ZIP inicial de las Lambdas (solo se usa al crearlas; después despliega CI).  |
| `cors_allow_origins` | Orígenes CORS del HTTP API. Vacío = sin CORS. `"*"` está prohibido en prod.  |
| `enable_swagger`     | Expone `/swagger` sin JWT. `true` en sbx; prohibido en prod.                  |
| `chat_model_id`      | Perfil de inferencia de Claude en Bedrock.                                   |

## Configuración inicial (una vez)

1. **Configurar credenciales de AWS** (`aws configure`, SSO o variables de
   entorno) para la cuenta de destino.
2. **Inicializar el backend de estado**, una vez por cuenta: es
   `DocLens.Infra/bootstrap/`, compartido por ambos repositorios.
3. **Aplicar `DocLens.Infra`** para el entorno (sin `edge`, ver su README) y
   ejecutar sus migraciones SQL (`DocLens.Infra/migrations`).
4. **Inicializar este módulo raíz** contra el mismo bucket:
   ```bash
   cd infra/terraform
   terraform init \
     -backend-config="bucket=<state_bucket de DocLens.Infra/bootstrap>" \
     -backend-config="key=lambda-processing/sbx/terraform.tfstate" \
     -backend-config="region=eu-west-1" \
     -backend-config="dynamodb_table=doclens-terraform-locks" \
     -backend-config="encrypt=true"
   ```
   Para prod, usar `key=lambda-processing/prod/terraform.tfstate`.
5. **Construir el ZIP inicial** al que apunta `lambda_zip_path`. Debe ser una
   publicación *self-contained* para `linux-x64` cuyo ejecutable se llame
   `bootstrap` y tenga permiso de ejecución (el runtime `provided.al2023` no
   incluye .NET). Desde la raíz del repositorio:
   ```bash
   dotnet publish src/DocLens.Lambda -c Release -r linux-x64 --self-contained true \
     -o src/DocLens.Lambda/bin/Release/net10.0/linux-x64/publish
   python3 scripts/package-lambda-zip.py \
     src/DocLens.Lambda/bin/Release/net10.0/linux-x64/publish \
     src/DocLens.Lambda/bin/Release/net10.0/linux-x64/publish/doclens-lambda.zip
   ```
   El script renombra el ejecutable a `bootstrap` y fija el bit de ejecución
   (también funciona en Windows, donde `Compress-Archive` no conserva permisos).
   Un `zip -j` de una publicación normal **no** funciona en `provided.al2023`.
6. **Planificar y aplicar**:
   ```bash
   terraform plan  -var-file=envs/sbx.tfvars
   terraform apply -var-file=envs/sbx.tfvars
   ```
7. **Volver a aplicar `DocLens.Infra`** una vez, para que `edge` lea
   `api_endpoint`.

## Despliegue del código

Tras la creación, el código lo despliega el workflow `CD`
(`UpdateFunctionCode` con `ci/doclens-lambda-<sha>.zip`). Las funciones tienen
`lifecycle { ignore_changes = [s3_key, source_code_hash] }`, de modo que un
`terraform apply` posterior no revierte el código desplegado por CI.

## Puntos pendientes conocidos

- La regla de EventBridge del resultado de GuardDuty es una reconstrucción
  aproximada del patrón del evento: verificar los nombres de campo con un
  evento real.
- La ruta "no limpia" de GuardDuty (`THREATS_FOUND`/`UNSCANNABLE` → `REJECTED`)
  sigue abierta en ADR-004/ADR-011 y no está conectada.
- La Lambda Processor solo marca la versión como `CLEAN`; la extracción
  asíncrona (OCR + Bedrock) todavía se hace de forma síncrona en
  `POST /api/documents/process`.
