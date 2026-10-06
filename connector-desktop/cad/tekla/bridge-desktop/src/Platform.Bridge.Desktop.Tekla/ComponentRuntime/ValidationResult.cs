// Результат IComponentSchema.Validate(request) — успех или список ошибок
// для возврата 400 INVALID_COMPONENT_PARAMETERS.

#nullable enable

using System.Collections.Generic;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class ValidationResult
    {
        public bool Ok { get; set; }
        public List<ValidationError> Errors { get; } = new();

        public static ValidationResult Success() => new() { Ok = true };

        public static ValidationResult Fail(string field, string message)
        {
            var r = new ValidationResult { Ok = false };
            r.Errors.Add(new ValidationError(field, message));
            return r;
        }

        public ValidationResult AddError(string field, string message)
        {
            Ok = false;
            Errors.Add(new ValidationError(field, message));
            return this;
        }
    }

    public sealed record ValidationError(string Field, string Message);
}
