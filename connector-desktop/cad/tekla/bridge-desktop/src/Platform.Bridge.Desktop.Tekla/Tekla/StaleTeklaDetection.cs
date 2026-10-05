// Определение "stale" remote-references на Tekla после её перезапуска.
//
// Симптомы (наблюдаемые в проде, до self-recovery):
//   InvalidOperationException: "Cannot find remote object with id <guid>"
//   System.Runtime.Remoting.RemotingException (Trimble Remoting протокол)
//   System.Net.Sockets.SocketException (TCP-канал к старому Tekla процессу)
//   NullReferenceException (mock-объект остался от мёртвой сессии)
//
// Возникает когда Bridge.Desktop держит handles на объекты прошлой Tekla
// session, а Tekla уже перезапустилась. Self-recovery: дропаем _model,
// new Tekla.Structures.Model.Model() — Trimble bootstrap привязывается к
// новому процессу Tekla; ретраим операцию один раз.

#nullable enable

using System;

namespace Platform.Bridge.Desktop.Tekla.Tekla
{
    internal static class StaleTeklaDetection
    {
        /// <summary>
        /// True если exception (или любой из его InnerException) — характерный
        /// признак мёртвых remote-references на Tekla. Worker дропает TeklaConnection
        /// и ретраит операцию один раз.
        /// </summary>
        public static bool IsStaleHandle(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (IsStaleSingle(e)) return true;
            }
            return false;
        }

        private static bool IsStaleSingle(Exception e)
        {
            switch (e)
            {
                case System.Runtime.Remoting.RemotingException:
                    return true;
                case System.Net.Sockets.SocketException:
                    return true;
                case InvalidOperationException ioe when ContainsStaleMessage(ioe.Message):
                    return true;
                case NullReferenceException:
                    // Mock/proxy остался от мёртвой сессии — почти всегда stale.
                    return true;
            }
            return false;
        }

        private static bool ContainsStaleMessage(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            // Trimble Remoting обычно бросает "Cannot find remote object with id ..."
            return message!.IndexOf("remote object", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("Cannot find", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
