using System;
using N2.Definitions;
using N2.Persistence;
using N2.Web;
using N2.Security;
using System.Collections.Generic;
using N2.Persistence.Finder;

namespace N2.Edit.Trash
{
	/// <summary>
	/// Can throw and restore items. Thrown items are moved to a trash 
	/// container item.
	/// </summary>
	public class TrashHandler : ITrashHandler
	{
		public const string TrashContainerName = "Trash";
		public const string FormerName = "FormerName";
		public const string FormerParent = "FormerParent";
		public const string FormerExpires = "FormerExpires";
		public const string DeletedDate = "DeletedDate";
		private readonly IPersister persister;
		private readonly IItemFinder finder;
		private readonly IDefinitionManager definitions;
		private readonly ISecurityManager security;
		private readonly IHost host;

		public TrashHandler(IPersister persister, IItemFinder finder, IDefinitionManager definitions, ISecurityManager security, IHost host)
		{
			this.finder = finder;
			this.persister = persister;
			this.definitions = definitions;
			this.security = security;
			this.host = host;
		}

        /// <summary>The container of thrown items.</summary>
		ITrashCan ITrashHandler.TrashContainer
		{
			get { return GetTrashContainer(false) as ITrashCan; }
		}

		/// <summary>Navigate for nodes rather than use database queries. This is less performant but more easily testable.</summary>
		public bool UseNavigationMode { get; set; }

		public TrashContainerItem GetTrashContainer(bool create)
		{
			ContentItem rootItem = persister.Get(host.CurrentSite.RootItemID);
			TrashContainerItem trashContainer = GetExistingContainer(rootItem);
			if (create && trashContainer == null)
			{
				trashContainer = definitions.CreateInstance<TrashContainerItem>(rootItem);
				trashContainer.Name = TrashContainerName;
				trashContainer.Title = TrashContainerName;
				trashContainer.Visible = false;
				trashContainer.AuthorizedRoles.Add(new AuthorizedRole(trashContainer, "admin"));
				trashContainer.AuthorizedRoles.Add(new AuthorizedRole(trashContainer, "Editors"));
				trashContainer.AuthorizedRoles.Add(new AuthorizedRole(trashContainer, "Administrators"));
				trashContainer.SortOrder = int.MaxValue - 1000000;
				persister.Save(trashContainer);
			}
			return trashContainer;
		}

		private TrashContainerItem GetExistingContainer(ContentItem rootItem)
		{
			if (UseNavigationMode)
				return rootItem.GetChild(TrashContainerName) as TrashContainerItem;
			
			var items = finder.Where.Parent.Eq(rootItem).And.Type.Eq(typeof(TrashContainerItem)).MaxResults(1).Select<TrashContainerItem>();
			return items.Count > 0 ? items[0] : null;
		}

        /// <summary>Checks if the trash is enabled, the item is not already thrown and for the NotThrowable attribute.</summary>
        /// <param name="affectedItem">The item to check.</param>
        /// <returns>True if the item may be thrown.</returns>
		public bool CanThrow(ContentItem affectedItem)
		{
			TrashContainerItem trash = GetTrashContainer(false);
            bool enabled = trash == null || trash.Enabled;
            bool alreadyThrown = IsInTrash(affectedItem);

			var throwables = (ThrowableAttribute[])affectedItem.GetContentType().GetCustomAttributes(typeof(ThrowableAttribute), true);
			bool throwable = throwables.Length == 0 || throwables[0].Throwable == AllowInTrash.Yes;
			return enabled && !alreadyThrown && throwable;
		}

        /// <summary>Throws an item in a way that it later may be restored to it's original location at a later stage.</summary>
        /// <param name="item">The item to throw.</param>
		public virtual void Throw(ContentItem item)
		{
            CancellableItemEventArgs args = Invoke<CancellableItemEventArgs>(ItemThrowing, new CancellableItemEventArgs(item));
            if (!args.Cancel)
            {
                item = args.AffectedItem;

                ExpireTrashedItem(item);
                item.AddTo(GetTrashContainer(true));

                try
                {
                    persister.Save(item);
                }
                catch (PermissionDeniedException ex)
                {
                    throw new PermissionDeniedException("Permission denied while moving item to trash. Try disabling security checks using N2.Context.Security or preventing items from beeing moved to the trash with the [NonThrowable] attribute", ex);
                }

                Invoke<ItemEventArgs>(ItemThrowed, new ItemEventArgs(item));
            }
		}

        /// <summary>Expires an item that has been thrown so that it's not accessible to external users.</summary>
        /// <param name="item">The item to restore.</param>
		public virtual void ExpireTrashedItem(ContentItem item)
		{
			item[FormerName] = item.Name;
			item[FormerParent] = item.Parent;
			item[FormerExpires] = item.Expires;
			item[DeletedDate] = DateTime.Now;
			item.Expires = DateTime.Now;
			item.Name = item.ID.ToString();

            foreach (ContentItem child in item.Children)
                ExpireTrashedItem(child);
		}

		/// <summary>Restores an item to the original location.</summary>
		/// <param name="item">The item to restore.</param>
		public virtual void Restore(ContentItem item)
		{
			ContentItem parent = (ContentItem)item["FormerParent"];
			RestoreValues(item);
            persister.Save(item);
			persister.Move(item, parent);
		}

		/// <summary>Removes expiry date and metadata set during throwing.</summary>
		/// <param name="item">The item to reset.</param>
		public virtual void RestoreValues(ContentItem item)
		{
			item.Name = (string)item["FormerName"];
			item.Expires = (DateTime?)item["FormerExpires"];

			item["FormerName"] = null;
			item["FormerParent"] = null;
			item["FormerExpires"] = null;
			item["DeletedDate"] = null;

            foreach (ContentItem child in item.Children)
                RestoreValues(child);
		}

        /// <summary>Determines wether an item has been thrown away.</summary>
        /// <param name="item">The item to check.</param>
        /// <returns>True if the item is in the scraps.</returns>
		public bool IsInTrash(ContentItem item)
		{
            TrashContainerItem trash = GetTrashContainer(false);
			return trash != null && Find.IsDescendantOrSelf(item, trash);
		}

        protected virtual T Invoke<T>(EventHandler<T> handler, T args)
            where T : ItemEventArgs
        {
            if (handler != null && args.AffectedItem.VersionOf == null)
                handler.Invoke(this, args);
            return args;
        }

		/// <summary>Delete items lying in trash for longer than the specified interval.</summary>
		public void PurgeOldItems()
		{
			TrashContainerItem trash = GetTrashContainer(false);
			if (trash == null) return;
			if (trash.PurgeInterval == TrashPurgeInterval.Never) return;

			DateTime tresholdDate = Utility.CurrentTime().AddDays(-(int)trash.PurgeInterval);
			IList<ContentItem> expiredItems = null;
			if (UseNavigationMode)
			{
				expiredItems = new List<ContentItem>();
				foreach (var item in Find.EnumerateChildren(trash))
				{
					var deletedDate = item[TrashHandler.DeletedDate];
					if (deletedDate != null && ((DateTime)deletedDate) <= tresholdDate)
						expiredItems.Add(item);
				}
			}
			else
			{
				expiredItems = finder.Where.Parent.Eq(trash)
					.And.Detail(TrashHandler.DeletedDate).Le(tresholdDate)
					.Select();
			}
			
			try
			{
				security.ScopeEnabled = false;
				foreach (var item in expiredItems)
				{
					persister.Delete(item);
				}	
			}
			finally
			{
				security.ScopeEnabled = true;
			}
		}

        /// <summary>Occurs before an item is thrown.</summary>
        public event EventHandler<CancellableItemEventArgs> ItemThrowing;
        /// <summary>Occurs after an item has been thrown.</summary>
        public event EventHandler<ItemEventArgs> ItemThrowed;

    }
}
