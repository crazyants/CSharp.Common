﻿using System.Threading.Tasks;

namespace Common.Extensions
{
    public static class TaskExtensions
    {
        /// <summary>
        /// Execute `Task` synchronously.
        /// </summary>
        public static T AsSyncronous<T>(this Task<T> task)
        {
            return task.ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Execute `Task` synchronously.
        /// </summary>
        public static void AsSyncronous(this Task task)
        {
            task.ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}
