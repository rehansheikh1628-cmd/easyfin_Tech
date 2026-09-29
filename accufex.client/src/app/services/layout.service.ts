import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

@Injectable({
  providedIn: 'root'
})
export class LayoutService {
  private readonly storageKey = 'accufex_sidebar_collapsed';
  private isCollapsedSubject = new BehaviorSubject<boolean>(this.getInitialState());
  public isCollapsed$: Observable<boolean> = this.isCollapsedSubject.asObservable();

  private getInitialState(): boolean {
    try {
      return localStorage.getItem(this.storageKey) === 'true';
    } catch {
      return false;
    }
  }

  get isCollapsed(): boolean {
    return this.isCollapsedSubject.value;
  }

  toggleCollapse(): void {
    this.setCollapsed(!this.isCollapsedSubject.value);
  }

  setCollapsed(collapsed: boolean): void {
    this.isCollapsedSubject.next(collapsed);
    try {
      localStorage.setItem(this.storageKey, String(collapsed));
    } catch {
      // Ignore storage write errors
    }
  }
}
