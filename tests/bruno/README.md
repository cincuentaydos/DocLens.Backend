# DocLens API — colección Bruno

Abrir esta carpeta directamente en [Bruno](https://www.usebruno.com/)
(File → Open Collection).

## Preparación

1. Seleccionar el entorno **sbx** (desplegable arriba a la derecha).
2. Abrir el entorno, **Configure**, y rellenar `testPassword` en las variables
   secretas. El valor está en el gestor de contraseñas del equipo y no se
   versiona a propósito (ver `environments/sbx.bru`).

## Prueba de humo

1. **Auth / Get Token**: inicia sesión con el usuario de prueba de Cognito
   `test@doclens.org` (`custom:tenantId=test-tenant`) y guarda el `IdToken`
   devuelto como variable de entorno para el resto de peticiones.
2. **Smoke / Swagger Health**: comprobación de disponibilidad sin
   autenticación (solo en sbx, donde `enable_swagger=true`).
3. **Smoke / Process Document**: llamada autenticada a
   `POST /api/documents/process`. El `documentId`/`s3Key` no corresponde a una
   versión preparada, así que responde `404` (o `409` si la versión aún no ha
   pasado el análisis de malware). Lo que demuestra: el autorizador JWT,
   `TenantMiddleware` y el handler están bien conectados (no es un fallo de
   autenticación ni de enrutamiento).

Ejecutar **Auth / Get Token** antes de **Smoke / Process Document**: la segunda
depende de `{{idToken}}`.
